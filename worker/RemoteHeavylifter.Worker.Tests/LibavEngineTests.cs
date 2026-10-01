using RemoteHeavylifter.Worker.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>The libav engine against the command line on the same clips: same frames, same sizes, same failures.</summary>
public sealed class LibavEngineTests(ClipFixture clips) : IClassFixture<ClipFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static double Mean(Image<Rgb24> image)
    {
        double sum = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (var p in rows.GetRowSpan(y))
                    sum += (p.R + p.G + p.B) / 3.0;
        });
        return sum / (image.Width * image.Height);
    }

    private static async Task<Image<Rgb24>?[]> Frames(IMediaEngine engine, MediaSource source, IReadOnlyList<double> timestamps, string workDir, string? preFilter = null) =>
        await engine.ExtractFramesAsync(source, timestamps, 160, preFilter, workDir, Ct);

    [Fact]
    public async Task Probe_matches_ffprobe()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var source = Specs.Local(clips.Clip30);
        Assert.Equal(await Engines.Cli.ProbeDurationAsync(source, Ct), await libav.ProbeDurationAsync(source, Ct), 0.01);
        Assert.Equal(0, await libav.ProbeDurationAsync(Specs.Local(_tmp["missing.mp4"]), Ct));
    }

    [Fact]
    public async Task Frames_are_the_ones_the_command_line_picks()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var clip = Ffmpeg.MakeLevelsClip(_tmp["levels.mp4"], 20);
        var source = Specs.Local(clip);
        // Sprite and phash timestamps, plus ones that fall between frames and right on a GOP boundary.
        var timestamps = Timing.PlanSprite(20).Timestamps.Concat(PhashGenerator.Timestamps(20, 25)).Concat([0.0, 1.99, 2.0, 2.02, 19.9])
            .Order().ToList();

        var expected = await Frames(Engines.Cli, source, timestamps, _tmp["cli"]);
        var actual = await Frames(libav, source, timestamps, _tmp["libav"]);
        try
        {
            for (var i = 0; i < timestamps.Count; i++)
            {
                Assert.True(expected[i] is not null, $"cli frame at {timestamps[i]}");
                Assert.True(actual[i] is not null, $"libav frame at {timestamps[i]}");
                Assert.Equal((expected[i]!.Width, expected[i]!.Height), (actual[i]!.Width, actual[i]!.Height));
                // Neighbouring frames differ by 3 levels; the same frame by at most 1 (YUV to RGB rounding, JPEG round trip).
                Assert.True(Math.Abs(Mean(expected[i]!) - Mean(actual[i]!)) < 1.5,
                    $"at {timestamps[i]} s: cli {Mean(expected[i]!):F2}, libav {Mean(actual[i]!):F2}");
            }
        }
        finally
        {
            foreach (var image in expected.Concat(actual))
                image?.Dispose();
        }
        Assert.False(Directory.Exists(_tmp["libav/frames"]));
    }

    [Fact]
    public async Task Past_the_end_there_is_no_frame()
    {
        var libav = LibavEngines.Require();
        var frames = await Frames(libav, Specs.Local(clips.Clip30), [29.0, 45.0], _tmp.Path);
        Assert.NotNull(frames[0]);
        Assert.Null(frames[1]);
        frames[0]!.Dispose();
    }

    [Fact]
    public async Task Sprite_and_vtt_match_the_command_line()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var source = Specs.Local(clips.Clip30);
        await SpriteGenerator.GenerateAsync(Engines.Cli, source, 30, Specs.Sprite("9_sprite.jpg"), _tmp["c"], _tmp["c.jpg"], _tmp["c.vtt"], Ct);
        await SpriteGenerator.GenerateAsync(libav, source, 30, Specs.Sprite("9_sprite.jpg"), _tmp["l"], _tmp["l.jpg"], _tmp["l.vtt"], Ct);
        Assert.Equal(await File.ReadAllTextAsync(_tmp["c.vtt"], Ct), await File.ReadAllTextAsync(_tmp["l.vtt"], Ct));
        Assert.Equal((640, 360), (Image.Identify(_tmp["l.jpg"]).Width, Image.Identify(_tmp["l.jpg"]).Height));
    }

    [Fact]
    public async Task Cover_and_its_filter_fallback()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var source = Specs.Local(clips.Clip30);
        await CoverGenerator.GenerateAsync(Engines.Cli, source, 30, Specs.Cover(), _tmp["c"], _tmp["c.jpg"], Ct);
        await CoverGenerator.GenerateAsync(libav, source, 30, Specs.Cover(), _tmp["l"], _tmp["l.jpg"], Ct);
        using (Image<Rgb24> cli = Image.Load<Rgb24>(_tmp["c.jpg"]), mine = Image.Load<Rgb24>(_tmp["l.jpg"]))
        {
            Assert.Equal((cli.Width, cli.Height), (mine.Width, mine.Height));
            Assert.True(Math.Abs(Mean(cli) - Mean(mine)) < 1.0);
        }

        var broken = Specs.Cover(filter: "definitely_not_a_filter=1");
        await CoverGenerator.GenerateAsync(libav, source, 30, broken, _tmp["l2"], _tmp["l2.jpg"], Ct);
        Assert.True(new FileInfo(_tmp["l2.jpg"]).Length > 0);
        await Assert.ThrowsAsync<MediaException>(() =>
            CoverGenerator.GenerateAsync(libav, source, 30, broken with { FallbackWithoutFilter = false }, _tmp["l3"], _tmp["l3.jpg"], Ct));
    }

    [Fact]
    public async Task Rotated_video_is_turned_upright_like_the_command_line()
    {
        var libav = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var rotated = _tmp["rotated.mp4"];
        Ffmpeg.Run("ffmpeg", "-v", "error", "-y", "-display_rotation", "90", "-i", clips.Clip30, "-c", "copy", rotated);
        var source = Specs.Local(rotated);
        await CoverGenerator.GenerateAsync(Engines.Cli, source, 30, Specs.Cover(), _tmp["c"], _tmp["c.jpg"], Ct);
        await CoverGenerator.GenerateAsync(libav, source, 30, Specs.Cover(), _tmp["l"], _tmp["l.jpg"], Ct);
        var cli = Image.Identify(_tmp["c.jpg"]);
        var mine = Image.Identify(_tmp["l.jpg"]);
        Assert.Equal((180, 320), (cli.Width, cli.Height));
        Assert.Equal((cli.Width, cli.Height), (mine.Width, mine.Height));
    }

    [Fact]
    public async Task Phash_is_computed()
    {
        var libav = LibavEngines.Require();
        var hash = await PhashGenerator.GenerateAsync(libav, Specs.Local(clips.Clip30), 30, new(), _tmp.Path, Ct);
        Assert.Matches("^[0-9a-f]{1,16}$", hash);
        var ex = await Assert.ThrowsAsync<MediaException>(() =>
            PhashGenerator.GenerateAsync(libav, Specs.Local(_tmp["missing.mp4"]), 30, new(), _tmp.Path, Ct));
        Assert.Contains("cannot open", ex.Message);
    }

    [Fact]
    public async Task Gpu_decoding_gives_the_same_frames_or_falls_back()
    {
        var libav = LibavEngines.Require();
        var clip = Ffmpeg.MakeLevelsClip(_tmp["levels.mp4"], 10);
        var timestamps = Timing.PlanSprite(10).Timestamps;
        var software = await Frames(libav, Specs.Local(clip), timestamps, _tmp["sw"]);
        // cuda on the default GPU (software when there is none), and a device that cannot exist.
        foreach (var device in new string?[] { null, "99" })
        {
            var hardware = await Frames(libav, Specs.Local(clip).WithHardwareDecode("cuda", device), timestamps, _tmp["hw"]);
            for (var i = 0; i < timestamps.Count; i++)
            {
                Assert.Equal((software[i]!.Width, software[i]!.Height), (hardware[i]!.Width, hardware[i]!.Height));
                Assert.True(Math.Abs(Mean(software[i]!) - Mean(hardware[i]!)) < 1.5,
                    $"device {device ?? "default"} at {timestamps[i]} s: software {Mean(software[i]!):F2}, gpu {Mean(hardware[i]!):F2}");
                hardware[i]!.Dispose();
            }
        }
        foreach (var image in software)
            image?.Dispose();
    }

    [Fact]
    public async Task Reads_from_cove_with_the_token()
    {
        var libav = LibavEngines.Require();
        await using var cove = await FakeCove.StartAsync(clips.Clip30);
        var source = MediaSource.ForCove(cove.Url, FakeCove.Token, cove.Length);
        Assert.Equal(30, await libav.ProbeDurationAsync(source, Ct), 0.1);
        await SpriteGenerator.GenerateAsync(libav, source, 30, Specs.Sprite("9_sprite.jpg"), _tmp.Path, _tmp["s.jpg"], _tmp["s.vtt"], Ct);
        Assert.All(cove.Requests, r => Assert.True(r.Authorized));

        var wrong = MediaSource.ForCove(cove.Url, "wrong", cove.Length);
        Assert.Equal(0, await libav.ProbeDurationAsync(wrong, Ct));
        await Assert.ThrowsAsync<MediaException>(() => CoverGenerator.GenerateAsync(libav, wrong, 30, Specs.Cover(), _tmp.Path, _tmp["c.jpg"], Ct));
    }

    [Fact]
    public async Task Cancellation_stops_a_running_extraction()
    {
        var libav = LibavEngines.Require();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var timestamps = Enumerable.Range(0, 2000).Select(i => i * 0.015).ToList();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            libav.ExtractFramesAsync(Specs.Local(clips.Clip30), timestamps, 160, null, _tmp.Path, cts.Token));
    }
}
