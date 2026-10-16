using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Intel GPUs with libav on Linux (VA-API and Quick Sync): needs a render node this user can open and libva ≥ 2.21
/// with the Intel driver for the bundled FFmpeg.</summary>
public sealed class VaapiTests(ClipFixture clips) : IClassFixture<ClipFixture>, IDisposable
{
    private const string RenderNode = "/dev/dri/renderD128";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDir _tmp = new();
    private readonly RecordingLogger _log = new();

    public void Dispose() => _tmp.Dispose();

    private static void RequireVaapi()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Openable(RenderNode), $"no readable {RenderNode}");
        LibavEngines.Require();
        Assert.SkipUnless(LibavLoader.SupportsHwAccel("vaapi") && LibavLoader.HasEncoder("h264_vaapi"), "libav has no vaapi");
        Ffmpeg.RequireOrSkip();
        Assert.SkipUnless(Environment.GetEnvironmentVariable("HL_TEST_VAAPI") == "1",
            "set HL_TEST_VAAPI=1 (the bundled FFmpeg aborts the process with libva older than 2.21)");
    }

    private static bool Openable(string path)
    {
        try
        {
            using var _ = File.OpenRead(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData("vaapi", "h264_vaapi", false)]
    [InlineData("vaapi", "h264_vaapi", true)]
    [InlineData("qsv", "h264_qsv", false)]
    [InlineData("qsv", "h264_qsv", true)]
    public async Task Gpu_encodes_previews(string accel, string encoder, bool hardwareDecode)
    {
        RequireVaapi();
        Assert.SkipUnless(LibavLoader.SupportsHwAccel(accel) && LibavLoader.HasEncoder(encoder), $"libav has no {accel}");
        LibavLoader.TryLoad(null, out var info, out _);
        var engine = new LibavMediaEngine(info!, new LibavEngineOptions(null, 1, encoder, []), _log);
        var source = Specs.Local(clips.Clip30);
        if (hardwareDecode)
            source = source.WithHardwareDecode(accel, null);
        await PreviewGenerator.GenerateAsync(engine, source, 30, Specs.Preview(), _tmp["p"], _tmp["p.mp4"], Ct);
        var stream = Ffmpeg.ProbeStream(_tmp["p.mp4"]);
        Assert.Equal((640, 360), (stream.Width, stream.Height));
        Assert.Equal(12 * 0.75, stream.Duration, 0.3);
        _log.AssertNoFallback();
    }

    /// <summary>A Quick Sync slot decodes and encodes on its own GPU's render node (found as the worker finds it).</summary>
    [Fact]
    public async Task Quick_sync_runs_on_the_intel_gpus_render_node()
    {
        RequireVaapi();
        Assert.SkipUnless(LibavLoader.SupportsHwAccel("qsv") && LibavLoader.HasEncoder("h264_qsv"), "libav has no qsv");
        var node = Hardware.LinuxGpuDetector.RenderNodes("/sys/class/drm").FirstOrDefault(n => n.Vendor == "0x8086");
        Assert.SkipWhen(node is null, "no Intel GPU");
        LibavLoader.TryLoad(null, out var info, out _);
        var engine = new LibavMediaEngine(info!, new LibavEngineOptions(null, 1, $"h264_qsv@{node!.Path}", [], null, ["h264"]), _log);
        await PreviewGenerator.GenerateAsync(engine, Specs.Local(clips.Clip30).WithHardwareDecode("qsv", node.Path), 30, Specs.Preview(),
            _tmp["p"], _tmp["p.mp4"], Ct);
        Assert.Equal((640, 360), (Ffmpeg.ProbeStream(_tmp["p.mp4"]).Width, Ffmpeg.ProbeStream(_tmp["p.mp4"]).Height));
        _log.AssertNoFallback();
    }
}
