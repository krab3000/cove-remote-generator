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

    public static async Task GenerateAsync(
        IMediaEngine engine, MediaSource src, double duration, SpriteSpec spec, string workDir, string spriteOutput, string vttOutput,
        CancellationToken ct, bool keyframes = false)
    {
        if (duration <= 0)
            throw new MediaException("sprite: unknown duration");
        var plan = Timing.PlanSprite(duration, spec.MaxFrames);
        Directory.CreateDirectory(workDir);
        var frames = await engine.ExtractFramesAsync(src, plan.Timestamps, spec.FrameWidth, spec.PreFilter, workDir, ct, keyframes);
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
