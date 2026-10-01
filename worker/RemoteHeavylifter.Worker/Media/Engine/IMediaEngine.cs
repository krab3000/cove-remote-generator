using RemoteHeavylifter.Protocol;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>
/// How the worker reads and encodes video: the ffmpeg/ffprobe command line (<see cref="CliMediaEngine"/>) or libav in
/// this process (<see cref="LibavMediaEngine"/>). The generators keep everything both share (plans, grids, hashes,
/// committing outputs); an engine only turns a source into frames, a cover file or a preview file.
/// </summary>
public interface IMediaEngine
{
    /// <summary>Shown in the startup log, e.g. "libav (FFmpeg n9.0.2) from D:\worker\ffmpeg".</summary>
    string Description { get; }

    /// <summary>Container duration in seconds, or 0 when it cannot be determined.</summary>
    Task<double> ProbeDurationAsync(MediaSource source, CancellationToken ct);

    /// <summary>Writes the cover JPEG for the frame at <paramref name="seek"/> to <paramref name="output"/>.</summary>
    Task CoverAsync(MediaSource source, double seek, CoverSpec spec, string workDir, string output, CancellationToken ct);

    /// <summary>One frame per timestamp, scaled to <paramref name="width"/> (after <paramref name="preFilter"/>); null
    /// where no frame could be decoded. The caller disposes the images.</summary>
    Task<Image<Rgb24>?[]> ExtractFramesAsync(
        MediaSource source, IReadOnlyList<double> timestamps, int width, string? preFilter, string workDir, CancellationToken ct);

    /// <summary>Encodes the preview described by <paramref name="plan"/> to <paramref name="output"/> (MP4).</summary>
    Task PreviewAsync(
        MediaSource source, double duration, PreviewPlan plan, PreviewSpec spec, string workDir, string output, CancellationToken ct);
}
