using System.Text.Json;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Workers;

/// <summary>Workers that dialed in with an unknown token, waiting to be trusted or dismissed.</summary>
public sealed class PendingWorkers(StoreHolder store, TimeProvider time)
{
    internal const string StoreKey = "pending-workers";
    private const int MaxPending = 50;
    private static readonly TimeSpan Forget = TimeSpan.FromDays(7);
    // A pending worker retries every ~10 s; don't rewrite the store on every attempt.
    private static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PendingWorker>? _cache;

    public async Task<IReadOnlyList<PendingWorker>> GetAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return [.. (await LoadAsync(ct)).OrderByDescending(p => p.LastSeen)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(string tokenHash, string? name, string? version, string? remoteAddress, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await _gate.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(ct);
            var index = list.FindIndex(p => WorkerTokens.HashEquals(p.TokenHash, tokenHash));
            var previous = index >= 0 ? list[index] : null;
            var entry = new PendingWorker(
                WorkerTokens.IdFromHash(tokenHash), tokenHash, Trim(name, 100), Trim(version, 40), remoteAddress,
                previous?.FirstSeen ?? now, now);
            if (previous is null)
                list.Add(entry);
            else
                list[index] = entry;

            list.RemoveAll(p => now - p.LastSeen > Forget);
            if (list.Count > MaxPending)
                list.RemoveRange(0, list.Count - MaxPending);

            var changed = previous is null || previous.Name != entry.Name || previous.RemoteAddress != entry.RemoteAddress
                || now - previous.LastSeen > PersistEvery;
            if (changed)
                await SaveAsync(list, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingWorker?> RemoveAsync(string workerTokenId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(ct);
            var entry = list.FirstOrDefault(p => string.Equals(p.WorkerTokenId, workerTokenId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return null;
            list.Remove(entry);
            await SaveAsync(list, ct);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Trim(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();

    private async Task SaveAsync(List<PendingWorker> list, CancellationToken ct)
        => await store.Store.SetAsync(StoreKey, JsonSerializer.Serialize(list, Json), ct);

    private async Task<List<PendingWorker>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null)
            return _cache;
        var json = await store.Store.GetAsync(StoreKey, ct);
        try
        {
            _cache = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<PendingWorker>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            _cache = [];
        }
        return _cache;
    }
}
