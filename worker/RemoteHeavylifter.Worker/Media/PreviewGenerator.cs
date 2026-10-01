using System.Globalization;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

public static class PreviewGenerator
{
    internal static readonly string[] ProfileArgs = ["-profile:v", "high", "-level", "4.2"];

    internal static string ScaleFilter(PreviewSpec spec) =>
        string.IsNullOrEmpty(spec.ScaleFilter) ? string.Create(CultureInfo.InvariantCulture, $"scale={spec.Width}:-2") : spec.ScaleFilter;

    internal static List<string> SingleArgs(
        IReadOnlyList<string> inputArgs, string encoder, MediaSource source, double duration, PreviewPlan plan, PreviewSpec spec, string output)
    {
        List<string> args = [.. inputArgs, "-v", "error", "-y", .. EncoderArgs.InputArgsForEncoder(encoder)];
        if (plan.UsableStart > 0)
            args.AddRange(["-ss", Timing.Fixed(plan.UsableStart, 2)]);
        args.AddRange(source.Input());
        if (plan.UsableDuration < duration)
            args.AddRange(["-t", Timing.Fixed(plan.UsableDuration, 2)]);
        args.AddRange(["-max_muxing_queue_size", "1024"]);
        args.AddRange(EncoderArgs.VideoEncodeArgs(encoder, spec.Crf, Timing.NormalizePreset(spec.Preset)));
        args.AddRange(["-vf", ScaleFilter(spec) + EncoderArgs.UploadChain(encoder), .. ProfileArgs]);
        if (!spec.Audio)
            args.Add("-an");
        args.Add(output);
        return args;
    }

    internal static string SplicedFilter(int segmentCount, string scale)
    {
        var parts = new List<string>();
        for (var i = 0; i < segmentCount; i++)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"[{i}:v:0]{scale},setsar=1,setpts=PTS-STARTPTS[v{i}];"));
        for (var i = 0; i < segmentCount; i++)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"[v{i}]"));
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"concat=n={segmentCount}:v=1:a=0[spliced];[spliced]null"));
        return string.Concat(parts);
    }

    internal static List<string> SplicedArgs(
        IReadOnlyList<string> inputArgs, string encoder, MediaSource source, PreviewPlan plan, PreviewSpec spec, string output)
    {
        List<string> args = [.. inputArgs, "-v", "error", "-y", .. EncoderArgs.InputArgsForEncoder(encoder)];
        foreach (var seek in plan.SeekTimes)
            args.AddRange(["-ss", Timing.Fixed(seek, 2), "-t", Timing.Fixed(plan.SegmentDuration, 2), .. source.Input()]);
        var graph = SplicedFilter(plan.SegmentCount, ScaleFilter(spec)) + EncoderArgs.UploadChain(encoder) + "[preview]";
        args.AddRange(["-max_muxing_queue_size", "1024", "-filter_complex", graph, "-map", "[preview]"]);
        args.AddRange(EncoderArgs.VideoEncodeArgs(encoder, spec.Crf, Timing.NormalizePreset(spec.Preset)));
        args.AddRange([.. ProfileArgs, "-an", output]);
        return args;
    }

    internal static List<string> ChunkArgs(
        IReadOnlyList<string> inputArgs, string encoder, MediaSource source, double seek, PreviewPlan plan, PreviewSpec spec, string output)
    {
        List<string> args =
        [
            .. inputArgs, "-v", "error", "-y", .. EncoderArgs.InputArgsForEncoder(encoder),
            "-ss", Timing.Fixed(seek, 2), .. source.Input(), "-t", Timing.Fixed(plan.SegmentDuration, 2),
            "-max_muxing_queue_size", "1024", .. EncoderArgs.VideoEncodeArgs(encoder, spec.Crf, Timing.NormalizePreset(spec.Preset)),
            "-vf", ScaleFilter(spec) + EncoderArgs.UploadChain(encoder), .. ProfileArgs,
        ];
        if (!spec.Audio)
            args.Add("-an");
        args.Add(output);
        return args;
    }

    internal static List<string> ConcatArgs(string listPath, string output) =>
        ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c:v", "copy", output];

    public static async Task GenerateAsync(
        IMediaEngine engine, MediaSource src, double duration, PreviewSpec spec, string workDir, string output, CancellationToken ct)
    {
        if (duration <= 0)
            throw new MediaException("preview: unknown duration");
        var plan = Timing.PlanPreview(duration, spec.Segments, spec.SegmentDuration, spec.ExcludeStart, spec.ExcludeEnd, spec.Audio)
            ?? throw new MediaException("preview: nothing left after start/end exclusions");

        Directory.CreateDirectory(workDir);
        var temp = Path.Combine(workDir, "preview.tmp.mp4");
        try
        {
            await engine.PreviewAsync(src, duration, plan, spec, workDir, temp, ct);
            Outputs.CommitOutput(temp, output);
        }
        finally
        {
            Outputs.RemoveQuietly(temp);
        }
    }
}
