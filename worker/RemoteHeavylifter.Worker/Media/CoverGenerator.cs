using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

public static class CoverGenerator
{
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
