using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Codecs libav has a GPU decoder for that must still be decoded in software (vc1_qsv crashes).</summary>
public sealed class HwSkipCodecsTests(ClipFixture clips) : IClassFixture<ClipFixture>
{
    [Fact]
    public void Skip_list_from_the_flag()
    {
        WorkerOptions Load(params string[] args) => WorkerOptions.Load(args, _ => null).Options;

        Assert.Empty(Load().HwSkipCodecs);
        Assert.Equal(["vc1", "mpeg2video"], Load("--hwaccel-skip-codecs", "VC1, mpeg2video").HwSkipCodecs);
    }

    [Fact]
    public void A_skipped_codec_opens_in_software()
    {
        LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        // An unknown hwaccel fails to open on the GPU, so opening at all proves the GPU was never tried.
        var source = Specs.Local(clips.Clip30).WithHardwareDecode("no-such-hwaccel", null);
        using var interrupt = new Interrupt(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(30), "test");

        Assert.Throws<HardwareDecodeException>(() => LibavInput.Open(source, new DecodeSettings(1, false), interrupt, NullLogger.Instance, "test"));
        using var input = LibavInput.Open(source, new DecodeSettings(1, false, HwSkipCodecs: ["h264"]), interrupt, NullLogger.Instance, "test");
        Assert.False(input.OnGpu);
        // A slot's GPU decodes only the codecs it reports.
        using var notListed = LibavInput.Open(source, new DecodeSettings(1, false, HwCodecs: ["hevc"]), interrupt, NullLogger.Instance, "test");
        Assert.False(notListed.OnGpu);
    }
}
