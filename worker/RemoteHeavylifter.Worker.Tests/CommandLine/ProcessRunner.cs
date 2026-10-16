using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RemoteHeavylifter.Worker.Media;

public sealed record ProcessResult(int? ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Ok => !TimedOut && ExitCode == 0;

    public string Summary(int limit = 500)
    {
        var text = (Stderr.Length > 0 ? Stderr : Stdout).Trim();
        if (TimedOut)
            text = $"timed out. {text}".Trim();
        else if (text.Length == 0)
            text = $"exit code {ExitCode}";
        if (text.Length <= limit)
            return text;
        // Do not cut a surrogate pair in half.
        var cut = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..cut];
    }
}

public static class ProcessRunner
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>Run a command (never through a shell). Timeouts and cancellation kill the whole process tree;
    /// a timeout returns a TimedOut result, cancellation rethrows.</summary>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    public static async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        for (var i = 1; i < args.Count; i++)
            info.ArgumentList.Add(args[i]);

        using var proc = new Process { StartInfo = info };
        proc.Start();
        // ffmpeg reads stdin for interactive commands; give it EOF instead of our console.
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            // Like communicate(): the timeout covers draining both pipes as well as the exit.
            await Task.WhenAll(stdout, stderr).WaitAsync(linked.Token);
            await proc.WaitForExitAsync(linked.Token);
            return new ProcessResult(proc.ExitCode, stdout.Result, stderr.Result, false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Kill(proc);
            await proc.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(proc.ExitCode, "", "", true);
        }
    }

    private static void Kill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
