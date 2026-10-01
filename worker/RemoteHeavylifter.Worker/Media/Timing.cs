using System.Globalization;
using System.Text;

namespace RemoteHeavylifter.Worker.Media;

public enum PreviewMode
{
    Single,
    Spliced,
    Chunks,
}

public sealed record PreviewPlan(
    PreviewMode Mode,
    int SegmentCount,
    double SegmentDuration,
    double UsableStart,
    double UsableEnd,
    double UsableDuration,
    IReadOnlyList<double> SeekTimes);

public sealed record SpritePlan(int FrameCount, int Cols, int Rows, double Interval, IReadOnlyList<double> Timestamps);

/// <summary>Pure timing math ported one-for-one from Cove's ThumbnailService / VideoFrameBatchExtractor.</summary>
/// <remarks>Source: yourcove/cove src/Cove.Api/Services/ThumbnailService.cs (commit f4cd955e).
/// Any change here must keep contract/parity/*.json passing.</remarks>
public static class Timing
{
    public const int DefaultPreviewSegments = 12;
    public const double DefaultPreviewSegmentDuration = 0.75;
    public const string FallbackPreviewPreset = "fast";
    private static readonly HashSet<string> Presets = ["ultrafast", "veryfast", "fast", "medium", "slow", "slower", "veryslow"];

    public const double SpriteMinDecodedRatio = 0.9;
    public const int SpriteBatchSize = 24;
    public const int SpriteMaxCommandLine = 24000;

    /// <summary>Cove's <c>ToString("F&lt;n&gt;", InvariantCulture)</c>: .NET formats the exact binary value and rounds
    /// exact ties half to even (0.125 -> "0.12"). Seeks are never negative in practice, but a value that rounds to
    /// zero is printed without a minus sign so a stray -0.0 cannot reach an ffmpeg argument.</summary>
    public static string Fixed(double value, int digits)
    {
        var text = value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return text.StartsWith('-') && text.AsSpan(1).Trim("0.").IsEmpty ? text[1..] : text;
    }

    public static string NormalizePreset(string? preset)
    {
        var candidate = (preset ?? "").Trim().ToLowerInvariant();
        return Presets.Contains(candidate) ? candidate : FallbackPreviewPreset;
    }

    public static double ParseExclusion(string? value, double duration)
    {
        if (string.IsNullOrWhiteSpace(value) || duration <= 0)
            return 0d;
        var trimmed = value.Trim();
        if (trimmed.EndsWith('%') && TryParse(trimmed[..^1].Trim(), out var percent))
            return Math.Clamp(duration * (percent / 100d), 0d, duration);
        return TryParse(trimmed, out var seconds) ? Math.Clamp(seconds, 0d, duration) : 0d;
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static double CoverSeek(double duration, double? requested = null)
    {
        var seek = requested ?? duration * 0.2;
        return seek <= 0 ? 1.0 : seek;
    }

    public static TimeSpan FrameDecodeTimeout(long sizeBytes)
    {
        var gigabytes = sizeBytes / (double)(1L << 30);
        return TimeSpan.FromSeconds(Math.Min(Math.Max(30.0 + 20.0 * gigabytes, 30.0), 150.0));
    }

    /// <summary>Null when there is nothing usable to encode (Cove fails the preview in that case).</summary>
    public static PreviewPlan? PlanPreview(
        double duration, int segments, double segmentDuration, string? excludeStart, string? excludeEnd, bool audio)
    {
        var segmentCount = Math.Min(Math.Max(segments <= 0 ? DefaultPreviewSegments : segments, 1), 100);
        var segDuration = Math.Min(Math.Max(segmentDuration <= 0 ? DefaultPreviewSegmentDuration : segmentDuration, 0.1), 30.0);
        var exStart = ParseExclusion(excludeStart, duration);
        var exEnd = ParseExclusion(excludeEnd, duration);
        var usableStart = Math.Min(exStart, Math.Max(0.0, duration - 0.1));
        var usableEnd = Math.Max(usableStart, duration - exEnd);
        var usable = usableEnd - usableStart;
        if (usable <= 0)
            return null;

        if (usable < segDuration * segmentCount)
            return new PreviewPlan(PreviewMode.Single, segmentCount, segDuration, usableStart, usableEnd, usable, []);

        var interval = usable / segmentCount;
        var seeks = new double[segmentCount];
        for (var i = 0; i < segmentCount; i++)
        {
            var seek = usableStart + interval * i + interval * 0.5;
            if (seek + segDuration > usableEnd)
                seek = usableEnd - segDuration;
            if (seek < usableStart)
                seek = usableStart;
            seeks[i] = seek;
        }
        var mode = audio ? PreviewMode.Chunks : PreviewMode.Spliced;
        return new PreviewPlan(mode, segmentCount, segDuration, usableStart, usableEnd, usable, seeks);
    }

    public static SpritePlan PlanSprite(double duration, int maxFrames = 81)
    {
        // Truncating division, as Cove's (int)(duration / 2); computed in double so huge durations cannot overflow.
        var frameCount = (int)Math.Min(maxFrames, Math.Max(1.0, Math.Truncate(duration / 2)));
        var cols = (int)Math.Ceiling(Math.Sqrt(frameCount));
        var rows = (frameCount + cols - 1) / cols;
        var interval = duration / frameCount;
        var timestamps = new double[frameCount];
        for (var i = 0; i < frameCount; i++)
            timestamps[i] = interval * (i + 0.5);
        return new SpritePlan(frameCount, cols, rows, interval, timestamps);
    }

    public static IReadOnlyList<(int Start, int Count)> PlanBatches(int perFrameArgLength, int count, int batchSize = SpriteBatchSize)
    {
        var affordable = Math.Max(1, SpriteMaxCommandLine / Math.Max(1, perFrameArgLength));
        var effective = Math.Min(Math.Max(1, batchSize), affordable);
        var batches = new List<(int, int)>();
        for (var start = 0; start < count; start += effective)
            batches.Add((start, Math.Min(effective, count - start)));
        return batches;
    }

    /// <summary>For each requested frame, the index of the decoded frame it shows; null when too few decoded.</summary>
    /// <remarks>Mirrors SpriteFrameGapFiller: nearest decoded frame, the earlier one winning a tie.</remarks>
    public static IReadOnlyList<int>? FillGaps(IReadOnlyList<bool> present)
    {
        var requested = present.Count;
        var decoded = present.Count(p => p);
        if (requested == 0 || decoded == 0 || decoded < Math.Ceiling(requested * SpriteMinDecodedRatio))
            return null;
        var sources = new List<int>(requested);
        for (var i = 0; i < requested; i++)
        {
            if (present[i])
            {
                sources.Add(i);
                continue;
            }
            for (var distance = 1; distance < requested; distance++)
            {
                int before = i - distance, after = i + distance;
                if (before >= 0 && present[before])
                {
                    sources.Add(before);
                    break;
                }
                if (after < requested && present[after])
                {
                    sources.Add(after);
                    break;
                }
            }
        }
        return sources;
    }

    public static string VttTime(double seconds)
    {
        // TimeSpan.FromSeconds truncates to 100ns ticks; the formatter then truncates to whole milliseconds.
        var ticks = (long)(seconds * 10_000_000);
        var totalMs = ticks / 10_000;
        var hours = totalMs / 3_600_000;
        var rem = totalMs % 3_600_000;
        var minutes = rem / 60_000;
        rem %= 60_000;
        var secs = rem / 1000;
        var millis = rem % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:D2}:{minutes:D2}:{secs:D2}.{millis:D3}");
    }

    public static string BuildVtt(
        string spriteFilename, int frameCount, int cols, double interval, int frameWidth, int frameHeight, double duration)
    {
        var text = new StringBuilder("WEBVTT\n\n");
        for (var i = 0; i < frameCount; i++)
        {
            var start = i * interval;
            var end = Math.Min((i + 1) * interval, duration);
            var x = i % cols * frameWidth;
            var y = i / cols * frameHeight;
            text.Append(CultureInfo.InvariantCulture, $"{VttTime(start)} --> {VttTime(end)}\n");
            text.Append(CultureInfo.InvariantCulture, $"{spriteFilename}#xywh={x},{y},{frameWidth},{frameHeight}\n\n");
        }
        return text.ToString();
    }
}
