using System.Globalization;

namespace RemoteHeavylifter.Worker.Media.Libav;

/// <summary>Seek decisions and timestamps, kept free of native calls so they can be tested.</summary>
public static class SeekPlanner
{
    /// <summary>Without an index, decoding forward beats seeking for targets at most this far ahead.</summary>
    public const double NoIndexForwardSeconds = 2.0;

    /// <summary>
    /// The seek time in microseconds the command line would use for <c>-ss</c>: the generators print seeks with
    /// <see cref="Timing.Fixed"/>, so the same rounding picks the same frame here; then, like fftools, the position is
    /// relative to the container's start time.
    /// </summary>
    public static long TargetMicroseconds(double seconds, int digits, long containerStartUs)
    {
        var rounded = double.Parse(Timing.Fixed(seconds, digits), CultureInfo.InvariantCulture);
        return (long)Math.Round(rounded * 1_000_000, MidpointRounding.AwayFromZero) + containerStartUs;
    }

    /// <summary>
    /// Whether reaching <paramref name="target"/> needs a seek, or decoding on from the last decoded frame
    /// (<paramref name="position"/>) gets there sooner. Decoding on is right when the keyframe the target's GOP starts
    /// at was already passed: a seek would only decode that GOP again. Never decodes backwards.
    /// </summary>
    /// <param name="keyframe">Timestamp of the last index entry at or before the target; null when the stream has no index.</param>
    /// <param name="noIndexReach">How far ahead (stream time base) decoding on is preferred when there is no index.</param>
    public static bool ShouldSeek(long? position, long? keyframe, long target, long noIndexReach)
    {
        if (position is not { } pos || target <= pos)
            return true;
        if (keyframe is { } kf)
            return kf > pos;
        return target - pos > noIndexReach;
    }
}

/// <summary>
/// fftools' autorotation (ffmpeg_filter.c: get_rotation + the filters configure_input_video_filter inserts): the
/// command line rotates by the stream's display matrix by default, so frames from libav must too.
/// </summary>
public static class AutoRotate
{
    /// <summary>av_display_rotation_get: the counter-clockwise rotation, in degrees, the matrix applies.</summary>
    public static double DisplayRotation(ReadOnlySpan<int> matrix)
    {
        static double Fixed(int value) => value / 65536.0;
        var scale0 = Math.Sqrt(Fixed(matrix[0]) * Fixed(matrix[0]) + Fixed(matrix[3]) * Fixed(matrix[3]));
        var scale1 = Math.Sqrt(Fixed(matrix[1]) * Fixed(matrix[1]) + Fixed(matrix[4]) * Fixed(matrix[4]));
        if (scale0 == 0 || scale1 == 0)
            return double.NaN;
        return -(Math.Atan2(Fixed(matrix[1]) / scale1, Fixed(matrix[0]) / scale0) * 180 / Math.PI);
    }

    /// <summary>fftools' get_rotation: the clockwise rotation to undo, in [0, 360).</summary>
    public static double Theta(ReadOnlySpan<int> matrix)
    {
        var rotation = DisplayRotation(matrix);
        var theta = double.IsNaN(rotation) ? 0 : -Math.Round(rotation, MidpointRounding.AwayFromZero);
        theta -= 360 * Math.Floor(theta / 360 + 0.9 / 360);
        return theta;
    }

    /// <summary>The filters (comma-joined, possibly empty) fftools inserts for this display matrix.</summary>
    public static string Filters(ReadOnlySpan<int> matrix)
    {
        if (matrix.Length < 9)
            return "";
        var theta = Theta(matrix);
        if (Math.Abs(theta - 90) < 1.0)
            return matrix[3] > 0 ? "transpose=cclock_flip" : "transpose=clock";
        if (Math.Abs(theta - 180) < 1.0)
        {
            var flips = new List<string>();
            if (matrix[0] < 0)
                flips.Add("hflip");
            if (matrix[4] < 0)
                flips.Add("vflip");
            return string.Join(",", flips);
        }
        if (Math.Abs(theta - 270) < 1.0)
            return matrix[3] < 0 ? "transpose=clock_flip" : "transpose=cclock";
        if (Math.Abs(theta) > 1.0)
            return string.Create(CultureInfo.InvariantCulture, $"rotate={theta:F6}*PI/180");
        return matrix[4] < 0 ? "vflip" : "";
    }
}
