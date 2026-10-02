using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Protocol;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>Runs ffmpeg/ffprobe processes, one command line per operation (the worker's original engine).</summary>
public sealed class CliMediaEngine(MediaContext ctx, ILogger? logger = null) : IMediaEngine
{
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConcatTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _log = logger ?? NullLogger.Instance;

    public MediaContext Context => ctx;

    public string Description => $"ffmpeg command line ({ctx.Ffmpeg})";

    public Task<double> ProbeDurationAsync(MediaSource source, CancellationToken ct) => MediaProbe.DurationAsync(ctx, source, ct);

    public async Task CoverAsync(MediaSource source, double seek, CoverSpec spec, string workDir, string output, CancellationToken ct)
    {
        var timeout = Timing.FrameDecodeTimeout(source.Size);
        var result = await ProcessRunner.RunAsync(
            [ctx.Ffmpeg, .. CoverGenerator.BuildCoverArgs(ctx.InputArgs, source, seek, spec.Filter, output)], timeout, ct);
        // A build without v360, or a layout the filter rejects, still gets the full frame.
        if (!result.Ok && !string.IsNullOrEmpty(spec.Filter) && spec.FallbackWithoutFilter)
            result = await ProcessRunner.RunAsync(
                [ctx.Ffmpeg, .. CoverGenerator.BuildCoverArgs(ctx.InputArgs, source, seek, null, output)], timeout, ct);
        if (!result.Ok)
            throw new MediaException($"cover: {result.Summary()}");
    }

    public async Task<Image<Rgb24>?[]> ExtractFramesAsync(
        MediaSource source, IReadOnlyList<double> timestamps, int width, string? preFilter, string workDir, CancellationToken ct,
        bool keyframes = false)
    {
        var frameDir = Path.Combine(workDir, "frames");
        var images = new Image<Rgb24>?[timestamps.Count];
        try
        {
            var paths = await ExtractFrameFilesAsync(source, timestamps, width, preFilter, frameDir, keyframes, ct);
            for (var i = 0; i < paths.Length; i++)
            {
                if (paths[i] is { } path)
                    images[i] = await Image.LoadAsync<Rgb24>(path, ct);
            }
            return images;
        }
        catch
        {
            foreach (var image in images)
                image?.Dispose();
            throw;
        }
        finally
        {
            // The frames are in memory now; their files are not needed any more.
            Outputs.RemoveDirQuietly(frameDir);
        }
    }

    /// <summary>JPEG files for the timestamps, extracted in batches of inputs per ffmpeg; null where a frame is missing.</summary>
    private async Task<string?[]> ExtractFrameFilesAsync(
        MediaSource source, IReadOnlyList<double> timestamps, int scaleWidth, string? preFilter, string frameDir, bool keyframes, CancellationToken ct)
    {
        Directory.CreateDirectory(frameDir);
        var length = SpriteGenerator.PerFrameArgLength(source, frameDir, timestamps.Count, scaleWidth, preFilter);
        var frames = new string?[timestamps.Count];
        // Every input of a batch opens its own decoder, which on a GPU holds video memory: keep hardware batches small.
        var batchSize = source.HardwareDecode ? Timing.HardwareSpriteBatchSize : Timing.SpriteBatchSize;
        foreach (var (start, count) in Timing.PlanBatches(length, timestamps.Count, batchSize))
        {
            var timeout = TimeSpan.FromSeconds(60 + 6 * count);
            var result = await ProcessRunner.RunAsync(
                [ctx.Ffmpeg, .. SpriteGenerator.SpriteBatchArgs(source, frameDir, timestamps, start, count, scaleWidth, preFilter, keyframes)], timeout, ct);
            if (!result.Ok && source.HardwareDecode)
            {
                _log.LogDebug("frame batch {First}-{Last} with hardware decoding: {Summary}; retrying in software",
                    start, start + count - 1, result.Summary(200));
                result = await ProcessRunner.RunAsync(
                    [ctx.Ffmpeg, .. SpriteGenerator.SpriteBatchArgs(source.Software, frameDir, timestamps, start, count, scaleWidth, preFilter, keyframes)],
                    timeout, ct);
            }
            if (!result.Ok)
            {
                // Frames that did decode are still on disk and still count.
                _log.LogDebug("frame batch {First}-{Last}: {Summary}", start, start + count - 1, result.Summary(200));
            }
            for (var index = start; index < start + count; index++)
            {
                var path = SpriteGenerator.FramePath(frameDir, index);
                if (Outputs.HasContent(path))
                    frames[index] = path;
            }
        }
        return frames;
    }

    public async Task PreviewAsync(
        MediaSource source, double duration, PreviewPlan plan, PreviewSpec spec, string workDir, string output, CancellationToken ct)
    {
        switch (plan.Mode)
        {
            case PreviewMode.Single:
                await EncodeAsync(enc => PreviewGenerator.SingleArgs(ctx.InputArgs, enc, source, duration, plan, spec, output), output, EncodeTimeout, ct);
                break;
            case PreviewMode.Spliced:
                await EncodeAsync(enc => PreviewGenerator.SplicedArgs(ctx.InputArgs, enc, source, plan, spec, output), output, EncodeTimeout, ct);
                break;
            default:
                await ChunkedAsync(source, plan, spec, workDir, output, ct);
                break;
        }
    }

    /// <summary>Hardware encode first when configured; any failure falls back to libx264 with a fresh command line.</summary>
    private async Task EncodeAsync(Func<string, List<string>> build, string output, TimeSpan timeout, CancellationToken ct)
    {
        if (ctx.Encoder != EncoderArgs.SoftwareEncoder)
        {
            var hw = await ProcessRunner.RunAsync([ctx.Ffmpeg, .. build(ctx.Encoder)], timeout, ct);
            if (hw.Ok && Outputs.HasContent(output))
                return;
            _log.LogDebug("hardware encode ({Encoder}) failed for {Output}; falling back to libx264: {Summary}",
                ctx.Encoder, Path.GetFileName(output), hw.Summary(200));
            Outputs.RemoveQuietly(output);
        }
        var result = await ProcessRunner.RunAsync([ctx.Ffmpeg, .. build(EncoderArgs.SoftwareEncoder)], timeout, ct);
        if (!result.Ok)
            throw new MediaException($"preview: {result.Summary()}");
    }

    private async Task ChunkedAsync(MediaSource src, PreviewPlan plan, PreviewSpec spec, string workDir, string output, CancellationToken ct)
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
                    await EncodeAsync(enc => PreviewGenerator.ChunkArgs(ctx.InputArgs, enc, src, seek, plan, spec, chunk), chunk, ChunkTimeout, ct);
                }
                catch (MediaException ex)
                {
                    _log.LogDebug("preview chunk {Index} failed: {Error}", i, ex.Message);
                }
            }

            var valid = chunks.Where(Outputs.HasContent).ToList();
            if (valid.Count == 0)
                throw new MediaException("preview: no usable chunks");
            var listPath = Path.Combine(chunkDir, "concat.txt");
            await File.WriteAllTextAsync(listPath, string.Join("\n", valid.Select(c =>
                "file '" + Path.GetFullPath(c).Replace('\\', '/').Replace("'", "'\\''") + "'")), ct);
            var result = await ProcessRunner.RunAsync([ctx.Ffmpeg, .. PreviewGenerator.ConcatArgs(listPath, output)], ConcatTimeout, ct);
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
