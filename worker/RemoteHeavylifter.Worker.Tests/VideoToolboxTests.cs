using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Apple VideoToolbox through the libav engine (Homebrew's FFmpeg on macOS); skipped elsewhere.</summary>
public sealed class VideoToolboxTests(ClipFixture clips) : IClassFixture<ClipFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private readonly RecordingLogger _log = new();

    /// <summary>An engine that records its log, so a test can tell real GPU decoding from a silent software fallback.</summary>
    private LibavMediaEngine RequireVideoToolbox(string encoder = "libx264")
    {
        LibavEngines.Require();
        Assert.SkipUnless(OperatingSystem.IsMacOS() && LibavLoader.SupportsHwAccel("videotoolbox"), "no VideoToolbox");
        LibavLoader.TryLoad(null, out var info, out _);
        return new LibavMediaEngine(info!, new LibavEngineOptions(null, 1, encoder, []), Engines.Cli, _log);
    }

    private void AssertNoFallback()
        => Assert.DoesNotContain(_log.Messages, m => m.Contains("in software", StringComparison.Ordinal) || m.Contains("libx264", StringComparison.Ordinal));

    private sealed class RecordingLogger : ILogger
    {
        public readonly ConcurrentQueue<string> Messages = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
    }

    [Fact]
    public async Task Hardware_decoded_phash_matches_software()
    {
        var libav = RequireVideoToolbox();
        var cpu = await PhashGenerator.GenerateAsync(libav, Specs.Local(clips.Clip30), 30, new(), _tmp["c"], Ct);
        var gpu = await PhashGenerator.GenerateAsync(libav, Specs.Local(clips.Clip30).WithHardwareDecode("videotoolbox", null), 30, new(), _tmp["g"], Ct);
        Assert.Equal(cpu, gpu);
        AssertNoFallback();
    }

    [Fact]
    public async Task Hardware_decoded_frames_match_software()
    {
        var libav = RequireVideoToolbox();
        var clip = Ffmpeg.MakeLevelsClip(_tmp["levels.mp4"], 10);
        var timestamps = Timing.PlanSprite(10).Timestamps;
        var cpu = await libav.ExtractFramesAsync(Specs.Local(clip), timestamps, 160, null, _tmp["sw"], Ct);
        var gpu = await libav.ExtractFramesAsync(Specs.Local(clip).WithHardwareDecode("videotoolbox", null), timestamps, 160, null, _tmp["hw"], Ct);
        var maxDiff = 0;
        for (var i = 0; i < timestamps.Count; i++)
        {
            Assert.Equal((cpu[i]!.Width, cpu[i]!.Height), (gpu[i]!.Width, gpu[i]!.Height));
            for (var y = 0; y < cpu[i]!.Height; y++)
            for (var x = 0; x < cpu[i]!.Width; x++)
            {
                Rgb24 a = cpu[i]![x, y], b = gpu[i]![x, y];
                maxDiff = Math.Max(maxDiff, Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))));
            }
            cpu[i]!.Dispose();
            gpu[i]!.Dispose();
        }
        // GPU frames are repacked to the software decoder's pixel format, so they are the same frames, not similar ones.
        Assert.Equal(0, maxDiff);
        AssertNoFallback();
    }

    [Fact]
    public async Task VideoToolbox_encodes_previews()
    {
        var engine = RequireVideoToolbox("h264_videotoolbox");
        Ffmpeg.RequireOrSkip();
        Assert.SkipUnless(LibavLoader.HasEncoder("h264_videotoolbox"), "no h264_videotoolbox");
        await PreviewGenerator.GenerateAsync(engine, Specs.Local(clips.Clip30).WithHardwareDecode("videotoolbox", null), 30,
            Specs.Preview(), _tmp["p"], _tmp["p.mp4"], Ct);
        var stream = Ffmpeg.ProbeStream(_tmp["p.mp4"]);
        Assert.Equal((640, 360), (stream.Width, stream.Height));
        Assert.Equal(12 * 0.75, stream.Duration, 0.3);
        AssertNoFallback();
    }
}
