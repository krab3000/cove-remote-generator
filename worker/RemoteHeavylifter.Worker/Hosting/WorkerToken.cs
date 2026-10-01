using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Hosting;

/// <summary>
/// The worker's only credential. Cove presents it when it dials the worker, the worker presents it when it dials
/// Cove, and every HTTP request to Cove carries it.
/// </summary>
public sealed class WorkerToken
{
    public const string FileName = "worker.token";

    private WorkerToken(string value, string source)
    {
        Value = value;
        Source = source;
        Hash = WorkerTokens.Hash(value);
        Id = WorkerTokens.IdFromHash(Hash);
    }

    public string Value { get; }
    public string Hash { get; }
    /// <summary>Short public ID, shown in Cove's UI so the token never has to be displayed.</summary>
    public string Id { get; }
    public string Source { get; }

    public bool Matches(string? presented) => !string.IsNullOrEmpty(presented) && WorkerTokens.Matches(presented, Hash);

    /// <summary><c>HL_WORKER_TOKEN</c> when set; otherwise the token in the data folder, created on first start.</summary>
    public static WorkerToken Load(WorkerOptions options)
    {
        if (options.Token is { } fixedToken)
            return new WorkerToken(fixedToken, "HL_WORKER_TOKEN");

        Directory.CreateDirectory(options.DataDir);
        var path = Path.Combine(options.DataDir, FileName);
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= WorkerTokens.MinLength } stored)
            return new WorkerToken(stored, path);

        var token = WorkerTokens.Generate();
        File.WriteAllText(path, token + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new WorkerToken(token, path + " (new)");
    }
}
