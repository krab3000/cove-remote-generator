using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Protocol;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RemoteHeavylifter.Worker.Media;

public static class SpriteGenerator
{
    internal static string FramePath(string frameDir, int index) =>
        Path.Combine(frameDir, string.Create(CultureInfo.InvariantCulture, $"frame_{index:D4}.jpg"));

    /// <summary>Command-line characters one more frame costs; the per-input options repeat for every frame.</summary>
    internal static int PerFrameArgLength(MediaSource source, string frameDir, int count, int scaleWidth, string? preFilter)
    {
        var last = FramePath(frameDir, Math.Max(0, count - 1));
        var sourceLength = source.Url.Length + source.InputOptions.Concat(source.DecodeOptions ?? []).Sum(o => o.Length + 1);
        var length = sourceLength + 28 + last.Length + 72 + (scaleWidth > 0 ? 22 : 0);
        return length + (preFilter is not null ? preFilter.Length + 1 : 0);
    }

    internal static List<string> SpriteBatchArgs(
        MediaSource source, string frameDir, IReadOnlyList<double> timestamps, int start, int count, int scaleWidth, string? preFilter)
    {
        List<string> args = ["-v", "error", "-y"];
        for (var offset = 0; offset < count; offset++)
        {
            var seconds = Math.Max(0.0, timestamps[start + offset]);
            args.AddRange(["-threads", "1", "-ss", Timing.Fixed(seconds, 3), .. source.Input()]);
        }
        var filters = new[] { preFilter, scaleWidth > 0 ? string.Create(CultureInfo.InvariantCulture, $"scale={scaleWidth}:-2") : null }
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList();
        for (var offset = 0; offset < count; offset++)
        {
            args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"{offset}:v:0"), "-an", "-frames:v", "1"]);
            if (filters.Count > 0)
                args.AddRange(["-vf", string.Join(",", filters)]);
            args.AddRange(["-threads", "1", "-q:v", "3", "-pix_fmt", "yuvj420p", FramePath(frameDir, start + offset)]);
        }
        return args;
    }

    internal static async Task<string?[]> ExtractFramesAsync(
        MediaContext ctx, MediaSource source, IReadOnlyList<double> timestamps, int scaleWidth, string? preFilter, string frameDir,
        ILogger log, CancellationToken ct)
    {
        Directory.CreateDirectory(frameDir);
        var length = PerFrameArgLength(source, frameDir, timestamps.Count, scaleWidth, preFilter);
        var frames = new string?[timestamps.Count];
        // Every input of a batch opens its own decoder, which on a GPU holds video memory: keep hardware batches small.
        var batchSize = source.HardwareDecode ? Timing.HardwareSpriteBatchSize : Timing.SpriteBatchSize;
        foreach (var (start, count) in Timing.PlanBatches(length, timestamps.Count, batchSize))
        {
            var timeout = TimeSpan.FromSeconds(60 + 6 * count);
            var result = await ProcessRunner.RunAsync(
                [ctx.Ffmpeg, .. SpriteBatchArgs(source, frameDir, timestamps, start, count, scaleWidth, preFilter)], timeout, ct);
            if (!result.Ok && source.HardwareDecode)
            {
                log.LogDebug("sprite batch {First}-{Last} with hardware decoding: {Summary}; retrying in software",
                    start, start + count - 1, result.Summary(200));
                result = await ProcessRunner.RunAsync(
                    [ctx.Ffmpeg, .. SpriteBatchArgs(source.Software, frameDir, timestamps, start, count, scaleWidth, preFilter)], timeout, ct);
            }
            if (!result.Ok)
            {
                // Frames that did decode are still on disk and still count.
                log.LogDebug("sprite batch {First}-{Last}: {Summary}", start, start + count - 1, result.Summary(200));
            }
            for (var index = start; index < start + count; index++)
            {
                var path = FramePath(frameDir, index);
                if (Outputs.HasContent(path))
                    frames[index] = path;
            }
        }
        return frames;
    }

    public static async Task GenerateAsync(
        MediaContext ctx, MediaSource src, double duration, SpriteSpec spec, string workDir, string spriteOutput, string vttOutput,
        CancellationToken ct, ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        if (duration <= 0)
            throw new MediaException("sprite: unknown duration");
        var plan = Timing.PlanSprite(duration, spec.MaxFrames);
        var frameDir = Path.Combine(workDir, "frames");
        var images = new Dictionary<int, Image<Rgb24>>();
        try
        {
            IReadOnlyList<int> sources;
            try
            {
                var frames = await ExtractFramesAsync(ctx, src, plan.Timestamps, spec.FrameWidth, spec.PreFilter, frameDir, log, ct);
                sources = Timing.FillGaps(frames.Select(f => f is not null).ToList())
                    ?? throw new MediaException($"sprite: only {frames.Count(f => f is not null)}/{plan.FrameCount} frames could be decoded");
                foreach (var index in sources.Distinct())
                    images[index] = await Image.LoadAsync<Rgb24>(frames[index]!, ct);
            }
            finally
            {
                // The frames are in memory now; their files are not needed any more.
                Outputs.RemoveDirQuietly(frameDir);
            }

            var first = images[sources[0]];
            int fw = first.Width, fh = first.Height;
            using var sheet = new Image<Rgb24>(fw * plan.Cols, fh * plan.Rows);
            sheet.Mutate(canvas =>
            {
                for (var i = 0; i < sources.Count; i++)
                    canvas.DrawImage(images[sources[i]], new Point(fw * (i % plan.Cols), fh * (i / plan.Cols)), 1f);
            });

            var spriteTmp = Path.Combine(workDir, "sprite.tmp.jpg");
            var vttTmp = Path.Combine(workDir, "thumbs.tmp.vtt");
            try
            {
                await sheet.SaveAsJpegAsync(spriteTmp, new JpegEncoder { Quality = 75 }, ct);
                var vtt = Timing.BuildVtt(spec.SpriteFilename, plan.FrameCount, plan.Cols, plan.Interval, fw, fh, duration);
                await File.WriteAllTextAsync(vttTmp, vtt, new UTF8Encoding(false), ct);
                Outputs.CommitOutput(spriteTmp, spriteOutput);
                Outputs.CommitOutput(vttTmp, vttOutput);
            }
            finally
            {
                Outputs.RemoveQuietly(spriteTmp);
                Outputs.RemoveQuietly(vttTmp);
            }
        }
        finally
        {
            foreach (var image in images.Values)
                image.Dispose();
        }
    }
}
