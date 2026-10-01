using System.Text.Json.Serialization;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Workers;

/// <summary>Which side opens the connection; derived from whether a worker URL is configured, never stored.</summary>
public static class WorkerConnections
{
    public const string CoveDials = "cove-dials";
    public const string WorkerDials = "worker-dials";
}

/// <summary>
/// A configured worker, as stored. The worker's token is the only credential: <see cref="TokenHash"/> identifies
/// it whichever side dials, and <see cref="Token"/> is kept only when Cove needs it to dial <see cref="Url"/>.
/// Never sent to the browser.
/// </summary>
public sealed record WorkerDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string TokenHash { get; init; }
    public string? Token { get; init; }
    public string? Url { get; init; }
    public string? CoveUrlOverride { get; init; }
    public bool Enabled { get; init; } = true;
    public int MaxConcurrency { get; init; } = 2;

    [JsonIgnore]
    public string WorkerTokenId => WorkerTokens.IdFromHash(TokenHash);

    [JsonIgnore]
    public bool CoveDials => !string.IsNullOrEmpty(Url);

    [JsonIgnore]
    public string Connection => CoveDials ? WorkerConnections.CoveDials : WorkerConnections.WorkerDials;
}

/// <summary>A worker as the UI sees it: the token is replaced by a hint.</summary>
public sealed record WorkerView(
    Guid Id,
    string Name,
    string WorkerTokenId,
    bool HasToken,
    string? TokenHint,
    string? Url,
    string? CoveUrlOverride,
    bool Enabled,
    int MaxConcurrency,
    string Connection)
{
    public static WorkerView From(WorkerDefinition worker) => new(
        worker.Id,
        worker.Name,
        worker.WorkerTokenId,
        worker.Token is { Length: > 0 },
        worker.Token is { Length: >= 8 } token ? "…" + token[^4..] : null,
        worker.Url,
        worker.CoveUrlOverride,
        worker.Enabled,
        worker.MaxConcurrency,
        worker.Connection);
}

/// <summary>A worker as the UI submits it. A null <see cref="Token"/> keeps the stored token (or hash).</summary>
public sealed record WorkerInput(
    Guid? Id,
    string? Name,
    string? Token,
    string? Url,
    string? CoveUrlOverride,
    bool Enabled = true,
    int MaxConcurrency = 2);

/// <summary>A worker that dialed in with a token no configured worker has; waits for someone to trust it.</summary>
public sealed record PendingWorker(
    string WorkerTokenId,
    string TokenHash,
    string? Name,
    string? Version,
    string? RemoteAddress,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

public sealed record PendingWorkerView(
    string WorkerTokenId,
    string? Name,
    string? Version,
    string? RemoteAddress,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    public static PendingWorkerView From(PendingWorker pending) => new(
        pending.WorkerTokenId, pending.Name, pending.Version, pending.RemoteAddress, pending.FirstSeen, pending.LastSeen);
}

public static class WorkerStates
{
    public const string Live = "live";
    public const string Offline = "offline";
    public const string Unauthorized = "unauthorized";
    public const string Incompatible = "incompatible";
    public const string Disabled = "disabled";
    public const string Waiting = "waiting";
}

/// <summary>A worker's connection state and what it reported at hello.</summary>
public sealed record WorkerHealthView(
    Guid Id,
    string Name,
    bool Enabled,
    string State,
    bool Live,
    string Connection,
    string? WorkerVersion,
    string? FfmpegVersion,
    string? Encoder,
    int? Capacity,
    int? Running,
    string? RemoteAddress,
    DateTimeOffset? ConnectedSince,
    string? Error,
    DateTimeOffset CheckedAt);

/// <summary>Extension-wide worker settings.</summary>
public sealed record WorkerSettings
{
    /// <summary>Base URL workers use to reach Cove's HTTP endpoints, e.g. http://192.168.1.10:5073.</summary>
    public string? CoveUrlForWorkers { get; init; }
}
