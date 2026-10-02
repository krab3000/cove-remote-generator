using FFmpeg.AutoGen.Abstractions;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Previews encoded in-process against the command line's, mode by mode.</summary>
public sealed class LibavPreviewTests(ClipFixture clips) : IClassFixture<ClipFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private async Task<(string Cli, string Libav)> BothAsync(IMediaEngine libav, string clip, double duration, Protocol.PreviewSpec spec)
    {
        string cli = _tmp["cli.mp4"], mine = _tmp["libav.mp4"];
        await PreviewGenerator.GenerateAsync(Engines.Cli, Specs.Local(clip), duration, spec, _tmp["c"], cli, Ct);
        await PreviewGenerator.GenerateAsync(libav, Specs.Local(clip), duration, spec, _tmp["l"], mine, Ct);
        return (cli, mine);
    }

    [Fact]
    public async Task Spliced_preview_matches_the_command_line()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var (cli, mine) = await BothAsync(libav, clips.Clip30, 30, Specs.Preview(preset: "ultrafast"));
        var expected = Ffmpeg.ProbeStream(cli);
        var actual = Ffmpeg.ProbeStream(mine);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(12 * 0.75, actual.Duration, 0.3);
        Assert.Equal(expected.Duration, actual.Duration, 0.15);
        Assert.Equal(["video"], Ffmpeg.Streams(mine).Select(s => s.Type));
    }

    [Fact]
    public async Task Single_preview_of_a_short_clip()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var shortClip = Ffmpeg.MakeClip(_tmp["2s.mp4"], 2);
        var (cli, mine) = await BothAsync(libav, shortClip, 2, Specs.Preview(preset: "ultrafast"));
        Assert.Equal(Ffmpeg.ProbeStream(cli).Duration, Ffmpeg.ProbeStream(mine).Duration, 0.15);
        Assert.Equal((640, 360), (Ffmpeg.ProbeStream(mine).Width, Ffmpeg.ProbeStream(mine).Height));
    }

    [Fact]
    public async Task Chunks_with_audio_keep_sound_in_sync()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var loud = Ffmpeg.MakeClip(_tmp["12s.mp4"], 12, audio: true);
        var spec = Specs.Preview(preset: "ultrafast", audio: true, segments: 4);
        var (cli, mine) = await BothAsync(libav, loud, 12, spec);

        Assert.Equal(Ffmpeg.ProbeStream(cli).Duration, Ffmpeg.ProbeStream(mine).Duration, 0.2);
        var streams = Ffmpeg.Streams(mine);
        Assert.Equal(["video", "audio"], streams.Select(s => s.Type));
        Assert.Equal(4 * 0.75, streams[0].Duration, 0.15);
        Assert.Equal(streams[0].Duration, streams[1].Duration, 0.1);
        Assert.False(Directory.Exists(_tmp["l/chunks"]));
    }

    [Fact]
    public async Task Single_preview_with_audio()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var loud = Ffmpeg.MakeClip(_tmp["2s.mp4"], 2, audio: true);
        var (_, mine) = await BothAsync(libav, loud, 2, Specs.Preview(preset: "ultrafast", audio: true));
        var streams = Ffmpeg.Streams(mine);
        Assert.Equal(["video", "audio"], streams.Select(s => s.Type));
        Assert.Equal(streams[0].Duration, streams[1].Duration, 0.1);
    }

    [Fact]
    public async Task Hardware_encoder_or_its_libx264_fallback()
    {
        LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        // h264_nvenc on an NVIDIA GPU; anywhere else it fails and the preview is encoded with libx264 instead.
        var nvenc = LibavEngines.WithEncoder("h264_nvenc");
        await PreviewGenerator.GenerateAsync(nvenc, Specs.Local(clips.Clip30), 30, Specs.Preview(), _tmp["n"], _tmp["n.mp4"], Ct);
        Assert.Equal((640, 360), (Ffmpeg.ProbeStream(_tmp["n.mp4"]).Width, Ffmpeg.ProbeStream(_tmp["n.mp4"]).Height));
        Assert.Equal(12 * 0.75, Ffmpeg.ProbeStream(_tmp["n.mp4"]).Duration, 0.3);

        // Quick Sync the same way: Intel graphics, or libx264.
        var qsv = LibavEngines.WithEncoder("h264_qsv");
        await PreviewGenerator.GenerateAsync(qsv, Specs.Local(clips.Clip30), 30, Specs.Preview(), _tmp["q"], _tmp["q.mp4"], Ct);
        Assert.Equal((640, 360), (Ffmpeg.ProbeStream(_tmp["q.mp4"]).Width, Ffmpeg.ProbeStream(_tmp["q.mp4"]).Height));
        Assert.Equal(12 * 0.75, Ffmpeg.ProbeStream(_tmp["q.mp4"]).Duration, 0.3);

        var bogus = LibavEngines.WithEncoder("h264_definitely_not");
        await PreviewGenerator.GenerateAsync(bogus, Specs.Local(clips.Clip30), 30, Specs.Preview(preset: "ultrafast"), _tmp["b"], _tmp["b.mp4"], Ct);
        Assert.True(new FileInfo(_tmp["b.mp4"]).Length > 0);
    }

    [Fact]
    public async Task Gpu_decoded_preview()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        foreach (var accel in new[] { "cuda", "qsv" })
        {
            var source = Specs.Local(clips.Clip30).WithHardwareDecode(accel, null);
            await PreviewGenerator.GenerateAsync(libav, source, 30, Specs.Preview(preset: "ultrafast"), _tmp[accel], _tmp[accel + ".mp4"], Ct);
            Assert.Equal(12 * 0.75, Ffmpeg.ProbeStream(_tmp[accel + ".mp4"]).Duration, 0.3);
        }
    }

    [Fact]
    public void Encoder_settings_follow_the_command_line()
    {
        var x264 = EncoderOptions.For("libx264", 21, "slow");
        Assert.Equal("libx264", x264.Codec);
        Assert.Equal("yuv420p", x264.PixelFormat);
        Assert.Equal([new("preset", "slow"), new("crf", "21"), new("profile", "high"), new("level", "4.2")], x264.Options);

        var nvenc = EncoderOptions.For("h264_nvenc", 23, "bogus");
        Assert.Equal("h264_nvenc", nvenc.Codec);
        Assert.Null(nvenc.PixelFormat);
        Assert.Equal([new("rc", "vbr"), new("cq", "23"), new("b", "0"), new("profile", "high"), new("level", "4.2")], nvenc.Options);

        var vt = EncoderOptions.For("h264_videotoolbox", 21, "fast");
        Assert.Equal(44, vt.QScale);
        Assert.DoesNotContain(vt.Options, o => o.Key == "q");
    }

    [Fact]
    public void Constant_frame_rate_drops_and_repeats()
    {
        var clock = new CfrClock(new AVRational { num = 25, den = 1 });
        clock.StartSegment();
        Assert.Equal(1, clock.Place(10.00)); // slot 0 (the segment starts at its first frame)
        Assert.Equal(1, clock.Place(10.04)); // slot 1
        Assert.Equal(0, clock.Place(10.05)); // lands on slot 1 again: dropped
        Assert.Equal(2, clock.Place(10.12)); // slot 3: repeated to fill slot 2
        Assert.Equal(4, clock.Next);
        Assert.Equal(4, clock.SegmentFrames);

        clock.StartSegment();
        Assert.Equal(1, clock.Place(50.0)); // a new segment continues at slot 4
        Assert.Equal(5, clock.Next);
        Assert.Equal(1, clock.SegmentFrames);
    }
}
