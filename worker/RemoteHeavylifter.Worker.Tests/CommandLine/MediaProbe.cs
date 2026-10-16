using System.ComponentModel;
using System.Globalization;

namespace RemoteHeavylifter.Worker.Media;

public static class MediaProbe
{
    internal static List<string> DurationArgs(MediaContext ctx, MediaSource source) =>
        [ctx.Ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", .. source.Software.Input()];

    /// <summary>Container duration in seconds, or 0 when it cannot be determined.</summary>
    public static async Task<double> DurationAsync(MediaContext ctx, MediaSource source, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(DurationArgs(ctx, source), TimeSpan.FromSeconds(30), ct);
        return result.Ok && double.TryParse(result.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 0.0;
    }

    /// <summary>The names <c>ffmpeg -hwaccels</c> lists (cuda, d3d11va, vaapi …); empty when ffmpeg cannot run.</summary>
    public static async Task<IReadOnlyList<string>> HwAccelsAsync(MediaContext ctx, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync([ctx.Ffmpeg, "-hide_banner", "-hwaccels"], TimeSpan.FromSeconds(10), ct);
            return result.Ok
                ? result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.EndsWith(':')).ToList()
                : [];
        }
        catch (Win32Exception)
        {
            return [];
        }
    }

    /// <summary>First line of <c>ffmpeg -version</c>, or null when ffmpeg is missing or broken.</summary>
    public static async Task<string?> FfmpegVersionAsync(MediaContext ctx, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync([ctx.Ffmpeg, "-hide_banner", "-version"], TimeSpan.FromSeconds(10), ct);
        }
        catch (Win32Exception)
        {
            return null;
        }
        var first = result.Stdout.Split('\n', 2)[0].Trim();
        return result.Ok && first.Length > 0 ? first : null;
    }
}
