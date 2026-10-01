using System.Text.Json;
using RemoteHeavylifter.Workers;

namespace RemoteHeavylifter.Generation;

/// <summary>The options of the last started run: the Generate panel's defaults and the task-list job's input.</summary>
public sealed class GenerationOptionsStore(StoreHolder store)
{
    internal const string StoreKey = "last-options";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GenerateRequest> GetAsync(CancellationToken ct = default)
    {
        var json = await store.Store.GetAsync(StoreKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return new GenerateRequest();
        try
        {
            return JsonSerializer.Deserialize<GenerateRequest>(json, Json) ?? new GenerateRequest();
        }
        catch (JsonException)
        {
            return new GenerateRequest();
        }
    }

    public Task SaveAsync(GenerateRequest request, CancellationToken ct = default)
        => store.Store.SetAsync(StoreKey, JsonSerializer.Serialize(request, Json), ct);
}
