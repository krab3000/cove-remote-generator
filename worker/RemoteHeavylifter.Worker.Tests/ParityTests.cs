using System.Text.Json.Nodes;
using RemoteHeavylifter.Worker.Media;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Ported from the former Python server's test_parity.py: golden cases from contract/parity plus hand-written shape checks.</summary>
public class ParityTests
{
    private static JsonNode P => Parity.Root;

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => (string)n!).ToArray();

    private static double[] Doubles(JsonNode? node) => node!.AsArray().Select(n => (double)n!).ToArray();

    private static void Near(double expected, double actual, double tolerance, string because) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{because}: expected {expected}, got {actual}");

    [Fact]
    public void FixedFormatsLikeDotnet()
    {
        foreach (var c in P["fixed"]!.AsArray())
            Assert.Equal((string)c!["expected"]!, Timing.Fixed((double)c["value"]!, (int)c["digits"]!));
    }

    [Fact]
    public void FixedNeverKeepsMinusZero()
    {
        Assert.Equal("0.00", Timing.Fixed(-0.001, 2));
        Assert.Equal("0.000", Timing.Fixed(-0.0, 3));
        Assert.Equal("-0.01", Timing.Fixed(-0.006, 2));
    }

    // Cove formats with ToString("F<n>"), which rounds exact binary ties half to even on .NET 10.
    [Theory]
    [InlineData(0.125, 2, "0.12")]
    [InlineData(0.375, 2, "0.38")]
    [InlineData(0.625, 2, "0.62")]
    [InlineData(2.5, 0, "2")]
    [InlineData(3.5, 0, "4")]
    [InlineData(1.0625, 3, "1.062")]
    [InlineData(1234.5678, 0, "1235")]
    public void FixedRoundsExactTiesToEvenLikeCove(double value, int digits, string expected) =>
        Assert.Equal(expected, Timing.Fixed(value, digits));

    [Fact]
    public void PreviewPlans()
    {
        foreach (var c in P["previewPlans"]!.AsArray())
        {
            var name = (string)c!["name"]!;
            var plan = Timing.PlanPreview((double)c["duration"]!, (int)c["segments"]!, (double)c["segmentDuration"]!,
                (string?)c["excludeStart"], (string?)c["excludeEnd"], (bool)c["audio"]!);
            var mode = (string?)c["mode"];
            if (mode is null)
            {
                Assert.True(plan is null, name);
                continue;
            }
            Assert.True(plan is not null, name);
            Assert.Equal(mode, plan.Mode.ToString().ToLowerInvariant());
            Near((double)c["usableStart"]!, plan.UsableStart, 1e-9, name);
            Near((double)c["usableDuration"]!, plan.UsableDuration, 1e-9, name);
            var seeks = Doubles(c["seeks"]);
            Assert.Equal(seeks.Length, plan.SeekTimes.Count);
            for (var i = 0; i < seeks.Length; i++)
                Near(seeks[i], plan.SeekTimes[i], 1e-6, $"{name} seek {i}");
        }
    }

    [Fact]
    public void SpritePlans()
    {
        foreach (var c in P["spritePlans"]!.AsArray())
        {
            var plan = Timing.PlanSprite((double)c!["duration"]!);
            Assert.Equal(((int)c["frameCount"]!, (int)c["cols"]!, (int)c["rows"]!), (plan.FrameCount, plan.Cols, plan.Rows));
            Near((double)c["interval"]!, plan.Interval, 1e-6, "interval");
            var first = Doubles(c["firstTimestamps"]);
            for (var i = 0; i < first.Length; i++)
                Near(first[i], plan.Timestamps[i], 1e-6, $"timestamp {i}");
            Near((double)c["lastTimestamp"]!, plan.Timestamps[^1], 1e-6, "last timestamp");
        }
    }

    [Fact]
    public void Vtt()
    {
        var v = P["vtt"]!;
        var text = Timing.BuildVtt((string)v["spriteFilename"]!, (int)v["frameCount"]!, (int)v["cols"]!, (double)v["interval"]!,
            (int)v["frameWidth"]!, (int)v["frameHeight"]!, (double)v["duration"]!);
        Assert.Equal((string)v["expected"]!, text);
        foreach (var c in P["vttTimes"]!.AsArray())
            Assert.Equal((string)c!["expected"]!, Timing.VttTime((double)c["seconds"]!));
    }

    [Fact]
    public void GapFill()
    {
        foreach (var c in P["gapFill"]!.AsArray())
        {
            var present = c!["present"]!.AsArray().Select(n => (bool)n!).ToList();
            var expected = c["expected"]?.AsArray().Select(n => (int)n!).ToArray();
            var actual = Timing.FillGaps(present);
            if (expected is null)
                Assert.Null(actual);
            else
                Assert.Equal(expected, actual!);
        }
    }

    [Fact]
    public void CoverArgs()
    {
        var c = P["coverArgs"]!;
        var args = CoverGenerator.BuildCoverArgs([], Specs.Local((string)c["source"]!), (double)c["seek"]!,
            (string?)c["filter"], (string)c["output"]!);
        Assert.Equal(Strings(c["expected"]), args);

        var withFilter = CoverGenerator.BuildCoverArgs(["-hwaccel", "auto"], Specs.Local("/s.mp4"), 1, "v360=input=e", "/o.jpg");
        Assert.Equal(["-hwaccel", "auto"], withFilter[..2]);
        Assert.Equal("v360=input=e", withFilter[withFilter.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void SplicedFilter()
    {
        var s = P["splicedFilter"]!;
        Assert.Equal((string)s["expected"]!, PreviewGenerator.SplicedFilter((int)s["segments"]!, (string)s["scale"]!));
    }

    [Fact]
    public void SplicedArgsShape()
    {
        var plan = Timing.PlanPreview(30, 12, 0.75, "0", "0", false)!;
        var args = PreviewGenerator.SplicedArgs([], "libx264", Specs.Local("/m/a.mp4"), plan, Specs.Preview(preset: "slow"), "/o/p.mp4");
        Assert.Equal(["-v", "error", "-y"], args[..3]);
        Assert.Equal(12, args.Count(a => a == "-i"));
        Assert.Equal(["-ss", "1.25", "-t", "0.75", "-i", "/m/a.mp4"], args[3..9]);
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.EndsWith("[spliced]null,format=yuv420p[preview]", graph);
        Assert.Equal(
            ["-map", "[preview]", "-c:v", "libx264", "-preset", "slow", "-crf", "21", "-pix_fmt", "yuv420p",
             "-profile:v", "high", "-level", "4.2", "-an", "/o/p.mp4"],
            args[args.IndexOf("-map")..]);
    }

    [Fact]
    public void SingleArgsTrimsOnlyWhenExcluded()
    {
        var spec = Specs.Preview();
        var plain = Timing.PlanPreview(5, 12, 0.75, "0", "0", false)!;
        var args = PreviewGenerator.SingleArgs([], "libx264", Specs.Local("/m/a.mp4"), 5, plain, spec, "/o/p.mp4");
        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-t", args);

        var trimmed = Timing.PlanPreview(5, 12, 0.75, "1", "1", false)!;
        args = PreviewGenerator.SingleArgs([], "h264_vaapi", Specs.Local("/m/a.mp4"), 5, trimmed, spec, "/o/p.mp4");
        Assert.Equal("1.00", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("3.00", args[args.IndexOf("-t") + 1]);
        Assert.Equal("scale=640:-2,format=nv12,hwupload", args[args.IndexOf("-vf") + 1]);
        Assert.Contains("-vaapi_device", args);
    }

    [Fact]
    public void BatchArgs()
    {
        var args = SpriteGenerator.SpriteBatchArgs(Specs.Local("/m/a.mp4"), "/tmp/f", [1.0, 3.0], 0, 2, 160, null);
        Assert.Equal(
            [
                "-v", "error", "-y",
                "-threads", "1", "-ss", "1.000", "-i", "/m/a.mp4",
                "-threads", "1", "-ss", "3.000", "-i", "/m/a.mp4",
                "-map", "0:v:0", "-an", "-frames:v", "1", "-vf", "scale=160:-2", "-threads", "1", "-q:v", "3", "-pix_fmt", "yuvj420p",
                SpriteGenerator.FramePath("/tmp/f", 0),
                "-map", "1:v:0", "-an", "-frames:v", "1", "-vf", "scale=160:-2", "-threads", "1", "-q:v", "3", "-pix_fmt", "yuvj420p",
                SpriteGenerator.FramePath("/tmp/f", 1),
            ],
            args);
        var vr = SpriteGenerator.SpriteBatchArgs(Specs.Local("/m/a.mp4"), "/tmp/f", [1.0], 0, 1, 160, "v360=input=he");
        Assert.Equal("v360=input=he,scale=160:-2", vr[vr.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void BatchPlanningRespectsCommandLineBudget()
    {
        Assert.Equal([(0, 24), (24, 24), (48, 24), (72, 9)], Timing.PlanBatches(100, 81));
        Assert.Equal(
            Enumerable.Range(0, 3).Select(i => (i * 12, Math.Min(12, 30 - i * 12))),
            Timing.PlanBatches(2000, 30));
    }

    [Fact]
    public void EncodersAndPresets()
    {
        Assert.Equal(["-c:v", "h264_nvenc", "-rc", "vbr", "-cq", "21", "-b:v", "0"], EncoderArgs.VideoEncodeArgs("h264_nvenc", 21, "slow"));
        Assert.Equal("44", EncoderArgs.VideoEncodeArgs("h264_videotoolbox", 21, "slow")[^1]);
        Assert.Equal("slow", Timing.NormalizePreset(" SLOW "));
        Assert.Equal("fast", Timing.NormalizePreset("superfast"));
        Assert.Equal(15, Timing.ParseExclusion("50%", 30));
        Assert.Equal(30, Timing.ParseExclusion("999", 30));
    }
}
