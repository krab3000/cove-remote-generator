using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Tasks;
using RemoteHeavylifter.Worker.Transport;

var options = WorkerOptions.FromEnvironment();
if (options.Validate() is { Count: > 0 } errors)
{
    foreach (var error in errors)
        Console.Error.WriteLine(error);
    return 2;
}

var token = WorkerToken.Load(options);

// Scratch from a previous run belongs to tasks no Cove is waiting for any more.
if (Directory.Exists(options.TasksDir))
    Directory.Delete(options.TasksDir, recursive: true);
Directory.CreateDirectory(options.TasksDir);

// Cove can dial the worker only when it listens; otherwise Kestrel binds just an ephemeral loopback port.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
// One line per request at Information would drown the task log.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// Without HL_LISTEN_URL the only listener is an unused loopback port; don't advertise it.
if (options.ListenUrl is null)
    builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
if (options.ListenUrl is not null)
    builder.WebHost.UseUrls(options.ListenUrl);
else
    builder.WebHost.UseUrls("http://127.0.0.1:0");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(token);
builder.Services.AddHttpClient<CoveHttpClient>(client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddSingleton<TaskRunner>();
builder.Services.AddSingleton<WorkerFacts>();
builder.Services.AddSingleton<CoveSession>();
if (options.CoveUrl is not null)
    builder.Services.AddHostedService<CoveConnector>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Worker");
var facts = app.Services.GetRequiredService<WorkerFacts>();
facts.FfmpegVersion = await MediaProbe.FfmpegVersionAsync(options.Media, CancellationToken.None);

log.LogInformation("Remote Heavylifter worker {Version} \"{Name}\": worker ID {Id} (token from {Source})",
    WorkerFacts.Version, options.Name, token.Id, token.Source);
log.LogInformation("{Capacity} parallel videos, encoder {Encoder}, {Ffmpeg}",
    options.MaxConcurrency, options.Encoder, facts.FfmpegVersion ?? "ffmpeg NOT FOUND");
if (options.ListenUrl is not null)
    log.LogInformation("Listening for Cove on {Url}{Path}", options.ListenUrl, ProtocolInfo.WorkerSocketPath);
if (options.CoveUrl is not null)
    log.LogInformation("Connecting to Cove at {Url}", options.CoveUrl);

if (options.ListenUrl is not null)
{
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
    app.Map(ProtocolInfo.WorkerSocketPath, async (HttpContext http, CoveSession session, ILogger<CoveSession> logger) =>
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // Cove proves itself with this worker's own token.
        if (!token.Matches(http.Request.Headers[ProtocolInfo.TokenHeader]))
        {
            logger.LogWarning("Refused a connection from {Peer}: wrong or missing worker token", http.Connection.RemoteIpAddress);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        await session.RunAsync(socket, http.Connection.RemoteIpAddress?.ToString() ?? "cove", http.RequestAborted);
    });
}

await app.RunAsync();
return 0;
