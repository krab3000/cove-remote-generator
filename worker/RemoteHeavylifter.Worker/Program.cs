using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Bench;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;
using RemoteHeavylifter.Worker.Tasks;
using RemoteHeavylifter.Worker.Transport;

// "bench" measures the GPUs and the CPU instead of serving Cove.
var bench = args.Length > 0 && args[0] == "bench";
var (options, argErrors, help) = WorkerOptions.Load(bench ? args[1..] : args);
if (help)
{
    Console.Write(WorkerOptions.Usage());
    return 0;
}
if (argErrors.Concat(options.Validate(bench)).ToList() is { Count: > 0 } errors)
{
    foreach (var error in errors)
        Console.Error.WriteLine(error);
    return 2;
}

// libva prints "libva info: …" for every VA-API connection (Quick Sync's runtime opens its own, past FFmpeg's log
// callback); errors only, unless set by the user. Before any libva connection is made.
NativeEnvironment.SetDefault("LIBVA_MESSAGING_LEVEL", "1");

using var startupLogs = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
}));
var startupLog = startupLogs.CreateLogger("Worker");

// Video is read, decoded and encoded by FFmpeg's libraries in this process.
var warnings = new List<string>();
if (!LibavLoader.TryLoad(options.FfmpegLibs, out var libav, out var libavFailure))
{
    Console.Error.WriteLine($"Cannot load FFmpeg's libraries: {libavFailure}. Set HL_FFMPEG_LIBS to a folder with FFmpeg 9.0's shared libraries.");
    return 2;
}

// Slot pools: one per GPU found (or listed with --hv-*-slots) and one for the CPU, each with its own engine.
var gpus = SlotBuilder.Detect(options, startupLog);
var (plans, planErrors) = SlotPlanner.Plan(options, gpus);
if (planErrors.Count > 0)
{
    foreach (var error in planErrors)
        Console.Error.WriteLine(error);
    return 2;
}
LibavLog.Install(startupLogs.CreateLogger("libav"));
var slots = SlotBuilder.Build(plans, options, libav!, startupLogs, warnings);

if (bench)
    return await BenchRunner.RunAsync(options, slots, startupLog, CancellationToken.None);

var token = WorkerToken.Load(options);

// Scratch from a previous run belongs to tasks no Cove is waiting for any more.
if (Directory.Exists(options.TasksDir))
    Directory.Delete(options.TasksDir, recursive: true);
Directory.CreateDirectory(options.TasksDir);

// Cove can dial the worker only when it listens. An ephemeral loopback port is always bound: ffmpeg reads task
// sources from the worker's SourceCache there.
// The worker's flags are its own (WorkerOptions), not ASP.NET configuration.
var builder = WebApplication.CreateBuilder();
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
const string LoopbackUrl = "http://127.0.0.1:0";
builder.WebHost.UseUrls(options.ListenUrl is not null ? [options.ListenUrl, LoopbackUrl] : [LoopbackUrl]);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(token);
builder.Services.AddSingleton(slots);
builder.Services.AddHttpClient<CoveHttpClient>(client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddSingleton(sp =>
{
    var cove = sp.GetRequiredService<CoveHttpClient>();
    return new SourceCache(options.SourceCacheMb * 1024L * 1024L, cove.GetRangeAsync);
});
builder.Services.AddSingleton<TaskRunner>();
builder.Services.AddSingleton<WorkerFacts>();
builder.Services.AddSingleton<CoveSession>();
if (options.CoveUrl is not null)
    builder.Services.AddHostedService<CoveConnector>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Worker");
var facts = app.Services.GetRequiredService<WorkerFacts>();
facts.FfmpegVersion = $"ffmpeg version {libav!.Version} (libav in-process)";

log.LogInformation("Remote Heavylifter worker {Version} \"{Name}\": worker ID {Id} (token from {Source})",
    WorkerFacts.Version, options.Name, token.Id, token.Source);
log.LogInformation("Sprite seek {SpriteSeek}, source cache {Cache}, {Ffmpeg}",
    options.SpriteSeek,
    options.SourceCacheMb > 0 ? $"{options.SourceCacheMb} MB" : "off",
    facts.FfmpegVersion);
foreach (var slot in slots.Slots)
    log.LogInformation("Slots {Slot}", SlotBuilder.Describe(slot));
log.LogInformation("Media engine: {Engine}", slots.Slots[0].Engine.Description);
foreach (var warning in warnings)
    log.LogWarning("{Warning}", warning);
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

var sourceCache = app.Services.GetRequiredService<SourceCache>();
app.MapMethods(SourceCache.PathPrefix + "{key}", [HttpMethods.Get, HttpMethods.Head],
    (HttpContext http, string key) => sourceCache.ServeAsync(http, key));
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
    var loopback = addresses.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var uri)
                                                 && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1");
    if (loopback is null)
        log.LogWarning("No loopback listener; ffmpeg will read sources from Cove directly");
    sourceCache.SetLocalBase(loopback);
});

await app.RunAsync();
return 0;
