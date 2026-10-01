using RemoteHeavylifter.Worker.Media;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>The one deliberate change from the Python server: per-input options precede every <c>-i</c> of the source.</summary>
public class InputOptionsTests
{
    private const string Url = "http://cove:9999/api/ext/heavylifter/worker/source/7";
    private static readonly MediaSource Source = MediaSource.ForCove(Url, "s3cret", 5L << 30);
    private static readonly string[] Options = [.. Source.InputOptions];

    /// <summary>Every <c>-i</c> reads the source URL and is directly preceded by the full option list; returns the -i count.</summary>
    private static int AssertOptionsBeforeEveryInput(IReadOnlyList<string> args)
    {
        var inputs = 0;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != "-i")
                continue;
            inputs++;
            Assert.Equal(Url, args[i + 1]);
            Assert.True(i >= Options.Length, "options missing before -i");
            Assert.Equal(Options, args.Skip(i - Options.Length).Take(Options.Length));
        }
        Assert.Equal(inputs, args.Count(a => a == "-headers"));
        return inputs;
    }

    [Fact]
    public void ForCoveBuildsTheExpectedOptions()
    {
        Assert.Equal(
            ["-headers", "X-Heavylifter-Token: s3cret\r\n", "-reconnect", "1", "-reconnect_on_network_error", "1",
             "-reconnect_delay_max", "5", "-rw_timeout", "30000000"],
            Options);
        Assert.Equal(Url, Source.Url);
        Assert.Equal(5L << 30, Source.Size);
    }

    [Fact]
    public void Cover()
    {
        var args = CoverGenerator.BuildCoverArgs(["-hwaccel", "auto"], Source, 12.5, "v360=input=e", "/o.jpg");
        Assert.Equal(1, AssertOptionsBeforeEveryInput(args));
        // Global input args still lead; the seek stays ahead of the per-input options.
        Assert.Equal(["-hwaccel", "auto"], args[..2]);
        var i = args.IndexOf("-i");
        Assert.Equal(["-ss", "12.50"], args[(i - Options.Length - 2)..(i - Options.Length)]);
    }

    [Fact]
    public void Spliced()
    {
        var plan = Timing.PlanPreview(30, 12, 0.75, "0", "0", false)!;
        var args = PreviewGenerator.SplicedArgs(["-hwaccel", "auto"], "libx264", Source, plan, Specs.Preview(), "/o/p.mp4");
        Assert.Equal(12, AssertOptionsBeforeEveryInput(args));
        Assert.Equal(["-hwaccel", "auto", "-v", "error", "-y", "-ss", "1.25", "-t", "0.75", .. Options, "-i", Url], args[..(11 + Options.Length)]);
    }

    [Fact]
    public void Single()
    {
        var plan = Timing.PlanPreview(5, 12, 0.75, "1", "1", false)!;
        var args = PreviewGenerator.SingleArgs([], "h264_vaapi", Source, 5, plan, Specs.Preview(), "/o/p.mp4");
        Assert.Equal(1, AssertOptionsBeforeEveryInput(args));
        Assert.Equal(
            ["-v", "error", "-y", "-vaapi_device", "/dev/dri/renderD128", "-ss", "1.00", .. Options, "-i", Url, "-t", "3.00"],
            args[..(11 + Options.Length)]);
    }

    [Fact]
    public void Chunk()
    {
        var plan = Timing.PlanPreview(10, 12, 0.75, "0", "0", true)!;
        var args = PreviewGenerator.ChunkArgs([], "libx264", Source, 2.5, plan, Specs.Preview(audio: true), "/o/c.mp4");
        Assert.Equal(1, AssertOptionsBeforeEveryInput(args));
        Assert.Equal(["-v", "error", "-y", "-ss", "2.50", .. Options, "-i", Url, "-t", "0.75"], args[..(9 + Options.Length)]);
        Assert.DoesNotContain("-an", args);
    }

    [Fact]
    public void SpriteBatch()
    {
        var timestamps = Enumerable.Range(0, 24).Select(i => i * 2.0 + 1).ToArray();
        var args = SpriteGenerator.SpriteBatchArgs(Source, "/tmp/f", timestamps, 0, 24, 160, null);
        Assert.Equal(24, AssertOptionsBeforeEveryInput(args));
        Assert.Equal(["-v", "error", "-y", "-threads", "1", "-ss", "1.000", .. Options, "-i", Url], args[..(9 + Options.Length)]);
    }

    [Fact]
    public void FfprobeDuration()
    {
        var args = MediaProbe.DurationArgs(MediaContext.Default, Source);
        Assert.Equal(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", .. Options, "-i", Url], args);
    }

    [Fact]
    public void ConcatIsNotASourceInput()
    {
        Assert.Equal(["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", "/w/list.txt", "-c:v", "copy", "/o.mp4"],
            PreviewGenerator.ConcatArgs("/w/list.txt", "/o.mp4"));
    }

    [Fact]
    public void BatchBudgetCountsInputOptions()
    {
        var plain = SpriteGenerator.PerFrameArgLength(Specs.Local(Url), "/tmp/f", 81, 160, null);
        var withOptions = SpriteGenerator.PerFrameArgLength(Source, "/tmp/f", 81, 160, null);
        Assert.Equal(plain + Options.Sum(o => o.Length + 1), withOptions);
        Assert.Equal(Url.Length + 28 + SpriteGenerator.FramePath("/tmp/f", 80).Length + 72 + 22, plain);

        // Long enough options shrink the batches below 24 frames.
        var bulky = new MediaSource(Url, [.. Options, "-user_agent", new string('x', 1500)], 0);
        var length = SpriteGenerator.PerFrameArgLength(bulky, "/tmp/f", 81, 160, null);
        var batches = Timing.PlanBatches(length, 81);
        Assert.All(batches, b => Assert.True(b.Count == 24000 / length || b.Start + b.Count == 81));
        Assert.True(batches[0].Count < 24);
    }

    [Fact]
    public void DecodeTimeoutUsesReportedSize()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Timing.FrameDecodeTimeout(0));
        Assert.Equal(TimeSpan.FromSeconds(30), Timing.FrameDecodeTimeout(-1));
        Assert.Equal(TimeSpan.FromSeconds(130), Timing.FrameDecodeTimeout(5L << 30));
        Assert.Equal(TimeSpan.FromSeconds(150), Timing.FrameDecodeTimeout(100L << 30));
    }
}
