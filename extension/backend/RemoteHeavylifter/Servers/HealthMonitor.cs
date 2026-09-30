using System.Collections.Concurrent;
using System.Diagnostics;
using RemoteHeavylifter.Contract;
using RemoteHeavylifter.Remote;

namespace RemoteHeavylifter.Servers;

public static class ServerStates
{
    public const string Live = "live";
    public const string Offline = "offline";
    public const string Unauthorized = "unauthorized";
    public const string Incompatible = "incompatible";
    public const string Disabled = "disabled";
}

public sealed record ServerHealth(
    Guid ServerId,
    string Name,
    bool Enabled,
    string State,
    long? LatencyMs,
    RemoteInfo? Info,
    string? Error,
    DateTimeOffset CheckedAt)
{
    public bool Live => State == ServerStates.Live;
}

/// <summary>Probes generation servers' <c>/v1/info</c>, caching each answer briefly.</summary>
public sealed class HealthMonitor(IRemoteClientFactory clients, TimeProvider time)
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, (string Fingerprint, ServerHealth Health)> _cache = new();

    public HealthMonitor(IRemoteClientFactory clients) : this(clients, TimeProvider.System)
    {
    }

    public async Task<IReadOnlyList<ServerHealth>> ProbeAsync(IEnumerable<ServerDefinition> servers, bool refresh, CancellationToken ct)
        => await Task.WhenAll(servers.Select(server => ProbeAsync(server, refresh, ct)));

    public async Task<ServerHealth> ProbeAsync(ServerDefinition server, bool refresh, CancellationToken ct, bool includeDisabled = false)
    {
        var now = time.GetUtcNow();
        if (!server.Enabled && !includeDisabled)
            return new ServerHealth(server.Id, server.Name, false, ServerStates.Disabled, null, null, null, now);

        var fingerprint = $"{server.BaseUrl}|{server.ApiKey.GetHashCode()}";
        if (!refresh
            && _cache.TryGetValue(server.Id, out var cached)
            && cached.Fingerprint == fingerprint
            && now - cached.Health.CheckedAt < CacheFor)
        {
            return cached.Health with { Enabled = server.Enabled, Name = server.Name };
        }

        var health = await ProbeUncachedAsync(server, ct);
        _cache[server.Id] = (fingerprint, health);
        return health;
    }

    public void Invalidate() => _cache.Clear();

    private async Task<ServerHealth> ProbeUncachedAsync(ServerDefinition server, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var info = await clients.Create(server).GetInfoAsync(ProbeTimeout, ct);
            var latency = stopwatch.ElapsedMilliseconds;
            if (info.ApiVersion != RemoteJson.ApiVersion)
            {
                return new ServerHealth(server.Id, server.Name, server.Enabled, ServerStates.Incompatible, latency, info,
                    $"Server speaks API v{info.ApiVersion}; this extension needs v{RemoteJson.ApiVersion}.", time.GetUtcNow());
            }
            return new ServerHealth(server.Id, server.Name, server.Enabled, ServerStates.Live, latency, info, null, time.GetUtcNow());
        }
        catch (RemoteException ex)
        {
            var state = ex.IsAuthFailure ? ServerStates.Unauthorized : ServerStates.Offline;
            var message = ex.IsAuthFailure ? "The server rejected the API key." : ex.Message;
            return new ServerHealth(server.Id, server.Name, server.Enabled, state, null, null, message, time.GetUtcNow());
        }
    }
}
