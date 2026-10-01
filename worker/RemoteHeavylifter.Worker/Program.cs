using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;
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

// libav in this process when its libraries load (HL_MEDIA_ENGINE=auto|libav), else ffmpeg/ffprobe processes.
var warnings = new List<string>();
LibavInfo? libav = null;
if (options.MediaEngine != "cli")
{
    if (!LibavLoader.TryLoad(options.FfmpegLibs, out libav, out var libavFailure))
    {
        if (options.MediaEngine == "libav")
        {
            Console.Error.WriteLine($"HL_MEDIA_ENGINE=libav, but {libavFailure}.");
            return 2;
        }
        warnings.Add($"libav unavailable ({libavFailure}); using the ffmpeg command line");
    }
}

// A hwaccel or encoder ffmpeg lacks would fail every attempt before its fallback: use the fallback from the start.
if (options.HwAccel is { } hwAccel
    && !(libav is not null
        ? LibavLoader.SupportsHwAccel(hwAccel)
        : (await MediaProbe.HwAccelsAsync(options.Media, CancellationToken.None)).Contains(hwAccel, StringComparer.OrdinalIgnoreCase)))
{
    warnings.Add($"ffmpeg has no \"{hwAccel}\" hwaccel; decoding in software");
    options = options with { HwAccel = null, HwAccelDevices = [] };
}
if (libav is not null && options.Encoder != EncoderArgs.SoftwareEncoder && !LibavLoader.HasEncoder(options.Encoder))
{
    warnings.Add($"ffmpeg has no {options.Encoder} encoder; encoding previews with {EncoderArgs.SoftwareEncoder}");
    options = options with { Encoder = EncoderArgs.SoftwareEncoder };
}

// Scratch from a previous run belongs to tasks no Cove is waiting for any more.
if (Directory.Exists(options.TasksDir))
    Directory.Delete(options.TasksDir, recursive: true);
Directory.CreateDirectory(options.TasksDir);

// Cove can dial the worker only when it listens. An ephemeral loopback port is always bound: ffmpeg reads task
// sources from the worker's SourceCache there.
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
const string LoopbackUrl = "http://127.0.0.1:0";
builder.WebHost.UseUrls(options.ListenUrl is not null ? [options.ListenUrl, LoopbackUrl] : [LoopbackUrl]);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(token);
builder.Services.AddHttpClient<CoveHttpClient>(client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddSingleton(sp =>
{
    var cove = sp.GetRequiredService<CoveHttpClient>();
    return new SourceCache(options.SourceCacheMb * 1024L * 1024L, cove.GetRangeAsync);
});
builder.Services.AddSingleton(sp => new CliMediaEngine(options.Media, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Media")));
builder.Services.AddSingleton<IMediaEngine>(sp =>
{
    var cli = sp.GetRequiredService<CliMediaEngine>();
    if (libav is null)
        return cli;
    var loggers = sp.GetRequiredService<ILoggerFactory>();
    var mediaLog = loggers.CreateLogger("Media");
    var formatOptions = LibavMediaEngine.FormatOptionsFrom(options.FfmpegInputArgs,
        ignored => mediaLog.LogWarning("HL_FFMPEG_INPUT_ARGS: -{Option} is ignored by the libav engine; use HL_HWACCEL", ignored));
    return new LibavMediaEngine(libav, new LibavEngineOptions(options.DecodeThreads, options.MaxConcurrency, options.Encoder, formatOptions), cli, mediaLog);
});
builder.Services.AddSingleton<TaskRunner>();
builder.Services.AddSingleton<WorkerFacts>();
builder.Services.AddSingleton<CoveSession>();
if (options.CoveUrl is not null)
    builder.Services.AddHostedService<CoveConnector>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Worker");
var facts = app.Services.GetRequiredService<WorkerFacts>();
if (libav is not null)
    LibavLog.Install(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("libav"));
facts.FfmpegVersion = libav is not null
    ? $"ffmpeg version {libav.Version} (libav in-process)"
    : await MediaProbe.FfmpegVersionAsync(options.Media, CancellationToken.None);
var engine = app.Services.GetRequiredService<IMediaEngine>();

log.LogInformation("Remote Heavylifter worker {Version} \"{Name}\": worker ID {Id} (token from {Source})",
    WorkerFacts.Version, options.Name, token.Id, token.Source);
log.LogInformation("{Capacity} parallel videos, encoder {Encoder}, decoding {Decode}, source cache {Cache}, {Ffmpeg}",
    options.MaxConcurrency, options.Encoder,
    options.HwAccel is null ? "software"
        : options.HwAccelDevices.Count > 0 ? $"{options.HwAccel} on devices {string.Join(", ", options.HwAccelDevices)}" : options.HwAccel,
    options.SourceCacheMb > 0 ? $"{options.SourceCacheMb} MB" : "off",
    facts.FfmpegVersion ?? "ffmpeg NOT FOUND");
log.LogInformation("Media engine: {Engine}", engine.Description);
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
