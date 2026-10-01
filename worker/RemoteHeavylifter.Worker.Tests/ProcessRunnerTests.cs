using System.Diagnostics;
using RemoteHeavylifter.Worker.Media;

namespace RemoteHeavylifter.Worker.Tests;

public class ProcessRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void SummaryPrefersStderrAndTruncates()
    {
        Assert.Equal("boom", new ProcessResult(1, "out", "  boom \n", false).Summary());
        Assert.Equal("out", new ProcessResult(1, "out", "", false).Summary());
        Assert.Equal("exit code 3", new ProcessResult(3, "", "", false).Summary());
        Assert.Equal("timed out.", new ProcessResult(null, "", "", true).Summary());
        Assert.Equal("timed out. late", new ProcessResult(null, "", "late", true).Summary());
        Assert.Equal(new string('x', 500), new ProcessResult(1, "", new string('x', 900), false).Summary());
        Assert.False(new ProcessResult(0, "", "", true).Ok);
        Assert.True(new ProcessResult(0, "", "", false).Ok);
    }

    [Fact]
    public async Task CapturesOutputAndClosesStdin()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/sh");
        // `cat` would hang forever if stdin were inherited rather than closed.
        var result = await ProcessRunner.RunAsync(["/bin/sh", "-c", "cat; echo out; echo err >&2; exit 4"], TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((4, "out\n", "err\n", false), (result.ExitCode, result.Stdout, result.Stderr, result.TimedOut));
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task TimeoutKillsTheProcessTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/sh");
        var watch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(["/bin/sh", "-c", "sleep 30 & sleep 30; wait"], TimeSpan.FromMilliseconds(300), Ct);
        Assert.True(result.TimedOut);
        Assert.False(result.Ok);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellationKillsAndThrows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/sh");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync(["/bin/sh", "-c", "sleep 30"], TimeSpan.FromSeconds(60), cts.Token));
    }

    [Fact]
    public async Task OutputsCommitAndHash()
    {
        var dir = Directory.CreateTempSubdirectory("hl-out-").FullName;
        try
        {
            var tmp = Path.Combine(dir, "a.tmp");
            var final = Path.Combine(dir, "a");
            await File.WriteAllTextAsync(tmp, "", Ct);
            Assert.Equal("ffmpeg produced no output", Assert.Throws<MediaException>(() => Outputs.CommitOutput(tmp, final)).Message);
            Assert.Throws<MediaException>(() => Outputs.CommitOutput(Path.Combine(dir, "missing"), final));
            await File.WriteAllTextAsync(tmp, "abc", Ct);
            await File.WriteAllTextAsync(final, "old", Ct);
            Outputs.CommitOutput(tmp, final);
            Assert.False(File.Exists(tmp));
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Outputs.Sha256File(final));
            Assert.Equal(Outputs.Sha256File(final), await Outputs.Sha256FileAsync(final, Ct));
            Outputs.RemoveQuietly(Path.Combine(dir, "missing"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
