using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

public static class CoverGenerator
{
    internal static List<string> BuildCoverArgs(
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

    public static async Task GenerateAsync(
        IMediaEngine engine, MediaSource src, double duration, CoverSpec spec, string workDir, string output, CancellationToken ct)
    {
        var seek = Timing.CoverSeek(duration, spec.SeekSeconds);
        // Scratch lives in the task's work dir (like preview/sprite) rather than beside the output.
        Directory.CreateDirectory(workDir);
        var temp = Path.Combine(workDir, "cover.tmp.jpg");
        try
        {
            await engine.CoverAsync(src, seek, spec, workDir, temp, ct);
            Outputs.CommitOutput(temp, output);
        }
        finally
        {
            Outputs.RemoveQuietly(temp);
        }
    }
}
