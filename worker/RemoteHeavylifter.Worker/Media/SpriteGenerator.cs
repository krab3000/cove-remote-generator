using System.Globalization;
using System.Text;
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

    public static async Task GenerateAsync(
        IMediaEngine engine, MediaSource src, double duration, SpriteSpec spec, string workDir, string spriteOutput, string vttOutput,
        CancellationToken ct)
    {
        if (duration <= 0)
            throw new MediaException("sprite: unknown duration");
        var plan = Timing.PlanSprite(duration, spec.MaxFrames);
        Directory.CreateDirectory(workDir);
        var frames = await engine.ExtractFramesAsync(src, plan.Timestamps, spec.FrameWidth, spec.PreFilter, workDir, ct);
        try
        {
            var sources = Timing.FillGaps(frames.Select(f => f is not null).ToList())
                ?? throw new MediaException($"sprite: only {frames.Count(f => f is not null)}/{plan.FrameCount} frames could be decoded");

            var first = frames[sources[0]]!;
            int fw = first.Width, fh = first.Height;
            using var sheet = new Image<Rgb24>(fw * plan.Cols, fh * plan.Rows);
            sheet.Mutate(canvas =>
            {
                for (var i = 0; i < sources.Count; i++)
                    canvas.DrawImage(frames[sources[i]]!, new Point(fw * (i % plan.Cols), fh * (i / plan.Cols)), 1f);
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
            foreach (var frame in frames)
                frame?.Dispose();
        }
    }
}
