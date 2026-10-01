using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Media;

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
