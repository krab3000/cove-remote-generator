using System.Globalization;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>
/// The ffmpeg command lines Cove runs (and the worker ran before it decoded with libav only): kept with the tests as
/// the reference the libav engine is compared with, frame for frame and option for option.
/// </summary>
public static class CommandLine
{
    public static List<string> BuildCoverArgs(
        IReadOnlyList<string> inputArgs, MediaSource source, double seek, string? videoFilter, string output)
    {
        List<string> args =
        [
            .. inputArgs, "-v", "error", "-fflags", "+discardcorrupt", "-err_detect", "ignore_err", "-y",
            "-ss", Timing.Fixed(seek, 2), .. source.Input(),
        ];
        if (!string.IsNullOrEmpty(videoFilter))
            args.AddRange(["-vf", videoFilter]);
        args.AddRange(["-vframes", "1", "-q:v", "2", "-f", "image2", output]);
        return args;
    }

    public static List<string> SingleArgs(
        IReadOnlyList<string> inputArgs, string encoder, MediaSource source, double duration, PreviewPlan plan, PreviewSpec spec, string output)
    {
        List<string> args = [.. inputArgs, "-v", "error", "-y", .. InputArgsForEncoder(encoder)];
        if (plan.UsableStart > 0)
            args.AddRange(["-ss", Timing.Fixed(plan.UsableStart, 2)]);
        args.AddRange(source.Input());
        if (plan.UsableDuration < duration)
            args.AddRange(["-t", Timing.Fixed(plan.UsableDuration, 2)]);
        args.AddRange(["-max_muxing_queue_size", "1024"]);
        args.AddRange(EncoderArgs.VideoEncodeArgs(encoder, spec.Crf, Timing.NormalizePreset(spec.Preset)));
        args.AddRange(["-vf", PreviewGenerator.ScaleFilter(spec) + EncoderArgs.UploadChain(encoder), .. PreviewGenerator.ProfileArgs]);
        if (!spec.Audio)
            args.Add("-an");
        args.Add(output);
        return args;
    }

    public static string SplicedFilter(int segmentCount, string scale)
    {
        var parts = new List<string>();
        for (var i = 0; i < segmentCount; i++)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"[{i}:v:0]{scale},setsar=1,setpts=PTS-STARTPTS[v{i}];"));
        for (var i = 0; i < segmentCount; i++)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"[v{i}]"));
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"concat=n={segmentCount}:v=1:a=0[spliced];[spliced]null"));
        return string.Concat(parts);
    }

    public static List<string> SplicedArgs(
        IReadOnlyList<string> inputArgs, string encoder, MediaSource source, PreviewPlan plan, PreviewSpec spec, string output)
    {
        List<string> args = [.. inputArgs, "-v", "error", "-y", .. InputArgsForEncoder(encoder)];
        foreach (var seek in plan.SeekTimes)
            args.AddRange(["-ss", Timing.Fixed(seek, 2), "-t", Timing.Fixed(plan.SegmentDuration, 2), .. source.Input()]);
        var graph = SplicedFilter(plan.SegmentCount, PreviewGenerator.ScaleFilter(spec)) + EncoderArgs.UploadChain(encoder) + "[preview]";
        args.AddRange(["-max_muxing_queue_size", "1024", "-filter_complex", graph, "-map", "[preview]"]);
        args.AddRange(EncoderArgs.VideoEncodeArgs(encoder, spec.Crf, Timing.NormalizePreset(spec.Preset)));
        args.AddRange([.. PreviewGenerator.ProfileArgs, "-an", output]);
        return args;
    }

    public static List<string> ChunkArgs(
        IReadOnlyList<string> inputArgs, string encoder, MediaSource source, double seek, PreviewPlan plan, PreviewSpec spec, string output)
    {
        List<string> args =
        [
            .. inputArgs, "-v", "error", "-y", .. InputArgsForEncoder(encoder),
            "-ss", Timing.Fixed(seek, 2), .. source.Input(), "-t", Timing.Fixed(plan.SegmentDuration, 2),
            "-max_muxing_queue_size", "1024", .. EncoderArgs.VideoEncodeArgs(encoder, spec.Crf, Timing.NormalizePreset(spec.Preset)),
            "-vf", PreviewGenerator.ScaleFilter(spec) + EncoderArgs.UploadChain(encoder), .. PreviewGenerator.ProfileArgs,
        ];
        if (!spec.Audio)
            args.Add("-an");
        args.Add(output);
        return args;
    }

    public static List<string> ConcatArgs(string listPath, string output) =>
        ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c:v", "copy", output];

    /// <summary>Command-line characters one more frame costs; the per-input options repeat for every frame.</summary>
    public static int PerFrameArgLength(MediaSource source, string frameDir, int count, int scaleWidth, string? preFilter)
    {
        var last = SpriteGenerator.FramePath(frameDir, Math.Max(0, count - 1));
        var sourceLength = source.Url.Length + source.InputOptions.Concat(source.DecodeOptions ?? []).Sum(o => o.Length + 1);
        var length = sourceLength + 28 + last.Length + 72 + (scaleWidth > 0 ? 22 : 0);
        return length + (preFilter is not null ? preFilter.Length + 1 : 0);
    }

    public static List<string> SpriteBatchArgs(
        MediaSource source, string frameDir, IReadOnlyList<double> timestamps, int start, int count, int scaleWidth, string? preFilter,
        bool keyframes = false)
    {
        List<string> args = ["-v", "error", "-y"];
        for (var offset = 0; offset < count; offset++)
        {
            var seconds = Math.Max(0.0, timestamps[start + offset]);
            args.AddRange(["-threads", "1"]);
            // Fast seek: the keyframe the seek lands on, and the decoder skips everything else.
            if (keyframes)
                args.AddRange(["-noaccurate_seek", "-skip_frame", "nokey"]);
            args.AddRange(["-ss", Timing.Fixed(seconds, 3), .. source.Input()]);
        }
        var filters = new[] { preFilter, scaleWidth > 0 ? string.Create(CultureInfo.InvariantCulture, $"scale={scaleWidth}:-2") : null }
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList();
        for (var offset = 0; offset < count; offset++)
        {
            args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"{offset}:v:0"), "-an", "-frames:v", "1"]);
            if (filters.Count > 0)
                args.AddRange(["-vf", string.Join(",", filters)]);
            args.AddRange(["-threads", "1", "-q:v", "3", "-pix_fmt", "yuvj420p", SpriteGenerator.FramePath(frameDir, start + offset)]);
        }
        return args;
    }

    public static IReadOnlyList<string> InputArgsForEncoder(string encoder) =>
        EncoderArgs.Split(encoder) is var (name, device) && name.EndsWith("_vaapi", StringComparison.Ordinal)
            ? ["-vaapi_device", device ?? EncoderArgs.DefaultVaapiDevice]
            : [];

    /// <summary><c>[..InputOptions, ..DecodeOptions, "-i", Url]</c>, the only way an input is ever added to an ffmpeg
    /// command line.</summary>
    public static IEnumerable<string> Input(this MediaSource source)
    {
        foreach (var option in source.InputOptions)
            yield return option;
        foreach (var option in source.DecodeOptions ?? [])
            yield return option;
        yield return "-i";
        yield return source.Url;
    }
}
