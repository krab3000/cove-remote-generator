using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>The libav engine's decisions that need no native code.</summary>
public class LibavLogicTests
{
    /// <summary>av_display_rotation_set: the matrix for a counter-clockwise rotation (ffmpeg's -display_rotation).</summary>
    private static int[] Rotation(double degrees)
    {
        var radians = -degrees * Math.PI / 180;
        int Fixed(double value) => (int)Math.Round(value * 65536);
        return [Fixed(Math.Cos(radians)), Fixed(-Math.Sin(radians)), 0, Fixed(Math.Sin(radians)), Fixed(Math.Cos(radians)), 0, 0, 0, 1 << 30];
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(90, "transpose=clock")]
    [InlineData(-90, "transpose=cclock")]
    [InlineData(270, "transpose=cclock")]
    [InlineData(180, "hflip,vflip")]
    [InlineData(45, "rotate=45.000000*PI/180")]
    public void Autorotation_matches_fftools(double degrees, string expected) =>
        Assert.Equal(expected, AutoRotate.Filters(Rotation(degrees)));

    [Fact]
    public void Mirrored_matrices_flip()
    {
        Assert.Equal("hflip", AutoRotate.Filters([-65536, 0, 0, 0, 65536, 0, 0, 0, 1 << 30]));
        Assert.Equal("vflip", AutoRotate.Filters([65536, 0, 0, 0, -65536, 0, 0, 0, 1 << 30]));
        Assert.Equal("", AutoRotate.Filters([]));
    }

    [Theory]
    [InlineData(2.5, 2, 1_400_000L, 3_900_000L)]
    [InlineData(0.125, 2, 0L, 120_000L)] // printed "0.12", as the command line gets it
    [InlineData(1.0005, 3, 0L, 1_000_000L)]
    [InlineData(12.3456, 3, 0L, 12_346_000L)]
    public void Seek_targets_use_the_command_lines_rounding(double seconds, int digits, long start, long expected) =>
        Assert.Equal(expected, SeekPlanner.TargetMicroseconds(seconds, digits, start));

    [Theory]
    [InlineData(null, 0L, 100L, true)] // nothing decoded yet
    [InlineData(500L, 0L, 400L, true)] // behind: never decode backwards
    [InlineData(500L, 500L, 500L, true)]
    [InlineData(500L, 300L, 900L, false)] // the target's GOP is already being decoded
    [InlineData(500L, 500L, 900L, false)]
    [InlineData(500L, 800L, 900L, true)] // a later keyframe: seeking skips decoding up to it
    public void Seeks_only_when_decoding_on_would_redo_work(long? position, long? keyframe, long target, bool seek) =>
        Assert.Equal(seek, SeekPlanner.ShouldSeek(position, keyframe, target, noIndexReach: 2000));

    [Fact]
    public void Without_an_index_nearby_targets_are_decoded_to()
    {
        Assert.False(SeekPlanner.ShouldSeek(1000, null, 2500, noIndexReach: 2000));
        Assert.True(SeekPlanner.ShouldSeek(1000, null, 3500, noIndexReach: 2000));
    }

    [Fact]
    public void Input_args_become_demuxer_options_without_hwaccel()
    {
        var ignored = new List<string>();
        var options = LibavMediaEngine.FormatOptionsFrom(
            ["-probesize", "5M", "-hwaccel", "cuda", "-analyzeduration", "10M", "-hwaccel_device", "1", "-seekable"], ignored.Add);
        Assert.Equal([new("probesize", "5M"), new("analyzeduration", "10M"), new("seekable", "1")], options);
        Assert.Equal(["hwaccel", "hwaccel_device"], ignored);
    }

    [Fact]
    public void Source_options_for_libav()
    {
        var source = MediaSource.ForCove("http://cove/s", "tok", 1).WithHardwareDecode("cuda", "1");
        Assert.Equal(
            [new("headers", "X-Heavylifter-Token: tok\r\n"), new("reconnect", "1"), new("reconnect_on_network_error", "1"),
             new("reconnect_delay_max", "5"), new("rw_timeout", "30000000")],
            source.ProtocolOptions());
        Assert.Equal(("cuda", "1"), source.HwDecode);
        Assert.Equal(("d3d11va", (string?)null), MediaSource.ForLocal("http://x", 1).WithHardwareDecode("d3d11va", null).HwDecode);
        Assert.Null(source.Software.HwDecode);
    }
}
