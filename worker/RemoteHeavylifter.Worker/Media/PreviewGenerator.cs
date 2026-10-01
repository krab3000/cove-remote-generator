using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

public static class PreviewGenerator
{
    internal static readonly string[] ProfileArgs = ["-profile:v", "high", "-level", "4.2"];

    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConcatTimeout = TimeSpan.FromSeconds(30);

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

    /// <summary>Hardware encode first when configured; any failure falls back to libx264 with a fresh command line.</summary>
    private static async Task EncodeAsync(
        MediaContext ctx, Func<string, List<string>> build, string output, TimeSpan timeout, ILogger log, CancellationToken ct)
    {
        if (ctx.Encoder != EncoderArgs.SoftwareEncoder)
        {
            var hw = await ProcessRunner.RunAsync([ctx.Ffmpeg, .. build(ctx.Encoder)], timeout, ct);
            if (hw.Ok && Outputs.HasContent(output))
                return;
            log.LogDebug("hardware encode ({Encoder}) failed for {Output}; falling back to libx264: {Summary}",
                ctx.Encoder, Path.GetFileName(output), hw.Summary(200));
            Outputs.RemoveQuietly(output);
        }
        var result = await ProcessRunner.RunAsync([ctx.Ffmpeg, .. build(EncoderArgs.SoftwareEncoder)], timeout, ct);
        if (!result.Ok)
            throw new MediaException($"preview: {result.Summary()}");
    }

    public static async Task GenerateAsync(
        MediaContext ctx, MediaSource src, double duration, PreviewSpec spec, string workDir, string output,
        CancellationToken ct, ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        if (duration <= 0)
            throw new MediaException("preview: unknown duration");
        var plan = Timing.PlanPreview(duration, spec.Segments, spec.SegmentDuration, spec.ExcludeStart, spec.ExcludeEnd, spec.Audio)
            ?? throw new MediaException("preview: nothing left after start/end exclusions");

        Directory.CreateDirectory(workDir);
        var temp = Path.Combine(workDir, "preview.tmp.mp4");
        try
        {
            switch (plan.Mode)
            {
                case PreviewMode.Single:
                    await EncodeAsync(ctx, enc => SingleArgs(ctx.InputArgs, enc, src, duration, plan, spec, temp), temp, EncodeTimeout, log, ct);
                    break;
                case PreviewMode.Spliced:
                    await EncodeAsync(ctx, enc => SplicedArgs(ctx.InputArgs, enc, src, plan, spec, temp), temp, EncodeTimeout, log, ct);
                    break;
                default:
                    await ChunkedAsync(ctx, src, plan, spec, workDir, temp, log, ct);
                    break;
            }
            Outputs.CommitOutput(temp, output);
        }
        finally
        {
            Outputs.RemoveQuietly(temp);
        }
    }

    private static async Task ChunkedAsync(
        MediaContext ctx, MediaSource src, PreviewPlan plan, PreviewSpec spec, string workDir, string output, ILogger log, CancellationToken ct)
    {
        var chunkDir = Path.Combine(workDir, "chunks");
        Directory.CreateDirectory(chunkDir);
        try
        {
            var chunks = new List<string>();
            for (var i = 0; i < plan.SeekTimes.Count; i++)
            {
                var seek = plan.SeekTimes[i];
                var chunk = Path.Combine(chunkDir, string.Create(CultureInfo.InvariantCulture, $"chunk_{i:D3}.mp4"));
                chunks.Add(chunk);
                try
                {
                    await EncodeAsync(ctx, enc => ChunkArgs(ctx.InputArgs, enc, src, seek, plan, spec, chunk), chunk, ChunkTimeout, log, ct);
                }
                catch (MediaException ex)
                {
                    log.LogDebug("preview chunk {Index} failed: {Error}", i, ex.Message);
                }
            }

            var valid = chunks.Where(Outputs.HasContent).ToList();
            if (valid.Count == 0)
                throw new MediaException("preview: no usable chunks");
            var listPath = Path.Combine(chunkDir, "concat.txt");
            await File.WriteAllTextAsync(listPath, string.Join("\n", valid.Select(c =>
                "file '" + Path.GetFullPath(c).Replace('\\', '/').Replace("'", "'\\''") + "'")), ct);
            var result = await ProcessRunner.RunAsync([ctx.Ffmpeg, .. ConcatArgs(listPath, output)], ConcatTimeout, ct);
            if (!result.Ok)
                throw new MediaException($"preview concat: {result.Summary()}");
        }
        finally
        {
            // The chunks are in the concatenated output now (or the preview failed).
            Outputs.RemoveDirQuietly(chunkDir);
        }
    }
}
