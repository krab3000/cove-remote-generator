using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;
using RemoteHeavylifter.Worker.Tasks;
using RemoteHeavylifter.Worker.Transport;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Spec factories with the defaults the Python server's models used.</summary>
internal static class Specs
{
    public static CoverSpec Cover(double? seekSeconds = null, string? filter = null, bool fallbackWithoutFilter = true) =>
        new(seekSeconds, filter, fallbackWithoutFilter);

    public static PreviewSpec Preview(
        int segments = 12, double segmentDuration = 0.75, string excludeStart = "0", string excludeEnd = "0",
        string preset = "slow", bool audio = false, int crf = 21, int width = 640, string? scaleFilter = null) =>
        new(segments, segmentDuration, excludeStart, excludeEnd, preset, audio, crf, width, scaleFilter);

    public static SpriteSpec Sprite(string spriteFilename, int maxFrames = 81, int frameWidth = 160, string? preFilter = null) =>
        new(maxFrames, frameWidth, preFilter, spriteFilename);

    /// <summary>A plain local source: no per-input options, so the original Python expectations hold verbatim.</summary>
    public static MediaSource Local(string path, long size = 0) => new(path, [], size);
}

/// <summary>The media engines the generator tests run against.</summary>
internal static class Engines
{
    public static IMediaEngine Cli { get; } = new CliMediaEngine(MediaContext.Default);
}

/// <summary>The libav engine over the shared libraries in HL_FFMPEG_LIBS or artifacts/ffmpeg/&lt;rid&gt;/ffmpeg
/// (scripts/fetch-ffmpeg.sh); tests using it skip when there are none.</summary>
internal static class LibavEngines
{
    private static readonly Lazy<(LibavMediaEngine? Engine, string Reason)> Loaded = new(() =>
    {
        var dir = Environment.GetEnvironmentVariable("HL_FFMPEG_LIBS") ?? FindBundled();
        return LibavLoader.TryLoad(dir, out var info, out var reason)
            ? (new LibavMediaEngine(info!, new LibavEngineOptions(null, 1, "libx264", []), Engines.Cli), "")
            : (null, reason);
    });

    public static LibavMediaEngine Require()
    {
        Assert.SkipUnless(Loaded.Value.Engine is not null, $"libav not available: {Loaded.Value.Reason}");
        return Loaded.Value.Engine!;
    }

    /// <summary>A libav engine that encodes previews with <paramref name="encoder"/>.</summary>
    public static LibavMediaEngine WithEncoder(string encoder)
    {
        Require();
        LibavLoader.TryLoad(null, out var info, out _);
        return new LibavMediaEngine(info!, new LibavEngineOptions(null, 1, encoder, []), Engines.Cli);
    }

    private static string? FindBundled()
    {
        var rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-"
                  + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "ffmpeg", rid, "ffmpeg");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }
}

internal static class Parity
{
    private static readonly Lazy<JsonNode> Data = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "parity", "cove-parity.json")))!);

    public static JsonNode Root => Data.Value;
}

internal static class Ffmpeg
{
    public static bool Available { get; } =
        Environment.GetEnvironmentVariable("HL_SKIP_FFMPEG_TESTS") != "1" && OnPath("ffmpeg") && OnPath("ffprobe");

    public static void RequireOrSkip() => Assert.SkipUnless(Available, "ffmpeg/ffprobe not on PATH");

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name + ".exe")));

    public static string Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var proc = Process.Start(info)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed: {stderr}");
        return stdout.Result;
    }

    public static string MakeClip(string path, double seconds, bool audio = false)
    {
        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        List<string> args = ["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size=320x180:rate=25:duration={duration}"];
        if (audio)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={duration}", "-c:a", "aac", "-shortest"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path]);
        Run("ffmpeg", [.. args]);
        return path;
    }

    /// <summary>Frame n is a flat grey of level 16 + (3·n mod 216): a frame's number can be read back from its pixels.
    /// H.264 with B-frames and a 2 s GOP, so seeks land mid-GOP.</summary>
    public static string MakeLevelsClip(string path, double seconds)
    {
        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", $"color=c=black:s=160x90:r=25:d={duration},format=gray,geq=lum='16+mod(3*N\\,216)'",
            "-c:v", "libx264", "-preset", "ultrafast", "-bf", "3", "-g", "50", "-pix_fmt", "yuv420p", path);
        return path;
    }

    /// <summary>Each stream's type and duration (seconds), in file order.</summary>
    public static List<(string Type, double Duration)> Streams(string path) =>
        Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_type,duration", "-of", "csv=p=0", path)
            .Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(','))
            .Select(parts => (parts[0], double.Parse(parts[1], CultureInfo.InvariantCulture)))
            .ToList();

    public static (int Width, int Height, double Duration) ProbeStream(string path)
    {
        var output = Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration",
            "-of", "default=nw=1:nk=1", path).Split((char[])['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(output[0], CultureInfo.InvariantCulture), int.Parse(output[1], CultureInfo.InvariantCulture),
            double.Parse(output[2], CultureInfo.InvariantCulture));
    }
}

/// <summary>One 30 s test clip shared by a test class, created on first use so a missing ffmpeg only skips.</summary>
public sealed class ClipFixture : IDisposable
{
    private readonly Lazy<string> _clip30;

    public ClipFixture()
    {
        Dir = Directory.CreateTempSubdirectory("hl-clips-").FullName;
        _clip30 = new Lazy<string>(() => Ffmpeg.MakeClip(Path.Combine(Dir, "30s.mp4"), 30));
    }

    public string Dir { get; }

    public string Clip30 => _clip30.Value;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A per-test scratch directory.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("hl-test-").FullName;

    public string this[string name] => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Serves a file the way Cove serves a source (token header, range requests).</summary>
internal sealed class FakeCove : IAsyncDisposable
{
    public const string Token = "t0ken";

    private readonly WebApplication _app;

    private FakeCove(WebApplication app) => _app = app;

    public ConcurrentQueue<(bool Authorized, string? Range)> Requests { get; } = new();

    public string Url => _app.Urls.First().TrimEnd('/') + "/source/7";

    public long Length { get; private set; }

    /// <summary>Serves <paramref name="clip"/> (any file) the way Cove serves a source.</summary>
    public static async Task<FakeCove> StartAsync(string clip)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var cove = new FakeCove(app) { Length = new FileInfo(clip).Length };
        app.MapGet("/source/7", (HttpContext http) =>
        {
            var authorized = http.Request.Headers[ProtocolInfo.TokenHeader] == Token;
            cove.Requests.Enqueue((authorized, http.Request.Headers.Range.FirstOrDefault()));
            return authorized ? Results.File(clip, "video/mp4", enableRangeProcessing: true) : Results.Unauthorized();
        });
        await app.StartAsync();
        return cove;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}


/// <summary>A worker's loopback <see cref="SourceCache"/> endpoint, reading from Cove with <paramref name="token"/>.</summary>
internal sealed class CacheHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _upstream;

    private CacheHost(WebApplication app, HttpClient upstream, SourceCache cache)
    {
        _app = app;
        _upstream = upstream;
        Cache = cache;
    }

    public SourceCache Cache { get; }

    public static async Task<CacheHost> StartAsync(long budgetBytes, string token = FakeCove.Token)
    {
        var upstream = new HttpClient();
        var cove = new CoveHttpClient(upstream, WorkerToken.Load(new WorkerOptions { Token = token }));
        var cache = new SourceCache(budgetBytes, cove.GetRangeAsync);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapMethods(SourceCache.PathPrefix + "{key}", [HttpMethods.Get, HttpMethods.Head],
            (HttpContext http, string key) => cache.ServeAsync(http, key));
        await app.StartAsync();
        cache.SetLocalBase(app.Urls.First());
        return new CacheHost(app, upstream, cache);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _upstream.Dispose();
    }
}
