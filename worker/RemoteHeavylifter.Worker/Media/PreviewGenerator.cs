using System.Globalization;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

public static class PreviewGenerator
{
    internal static readonly string[] ProfileArgs = ["-profile:v", "high", "-level", "4.2"];

    internal static string ScaleFilter(PreviewSpec spec) =>
        string.IsNullOrEmpty(spec.ScaleFilter) ? string.Create(CultureInfo.InvariantCulture, $"scale={spec.Width}:-2") : spec.ScaleFilter;

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
