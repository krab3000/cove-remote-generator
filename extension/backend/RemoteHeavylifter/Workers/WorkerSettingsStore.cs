using System.Text.Json;

namespace RemoteHeavylifter.Workers;

/// <summary>Extension-wide worker settings, persisted in the extension store.</summary>
public sealed class WorkerSettingsStore(StoreHolder store)
{
    internal const string StoreKey = "settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private volatile WorkerSettings? _cache;

    public async Task<WorkerSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cache is { } cached)
            return cached;
        var json = await store.Store.GetAsync(StoreKey, ct);
        try
        {
            return _cache = string.IsNullOrWhiteSpace(json) ? new WorkerSettings() : JsonSerializer.Deserialize<WorkerSettings>(json, Json) ?? new WorkerSettings();
        }
        catch (JsonException)
        {
            return _cache = new WorkerSettings();
        }
    }

    /// <summary>Validate and save. Returns the errors, or none on success.</summary>
    public async Task<IReadOnlyList<string>> SaveAsync(WorkerSettings input, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(input.CoveUrlForWorkers) ? null : input.CoveUrlForWorkers.Trim().TrimEnd('/');
        if (url is not null && !WorkerRegistry.IsHttpUrl(url))
            return ["The Cove URL for workers must be an absolute http:// or https:// address."];

        var settings = input with { CoveUrlForWorkers = url };
        await store.Store.SetAsync(StoreKey, JsonSerializer.Serialize(settings, Json), ct);
        _cache = settings;
        return [];
    }
}
