using System.Text.Json;

namespace RemoteHeavylifter.Servers;

/// <summary>The configured generation servers, persisted as JSON in the extension store.</summary>
public sealed class ServerRegistry(StoreHolder store)
{
    internal const string StoreKey = "servers";
    public const int MaxConcurrencyLimit = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ServerDefinition>? _cache;

    public async Task<IReadOnlyList<ServerDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        if (_cache is { } cached)
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            return _cache ??= await LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replace the whole list. Returns the validation errors, or none on success.</summary>
    public async Task<(IReadOnlyList<ServerDefinition> Servers, IReadOnlyList<string> Errors)> ReplaceAsync(
        IReadOnlyList<ServerInput> input,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = _cache ?? await LoadAsync(ct);
            var (servers, errors) = Merge(input, current);
            if (errors.Count > 0)
                return (current, errors);

            await store.Store.SetAsync(StoreKey, JsonSerializer.Serialize(servers, Json), ct);
            _cache = servers;
            return (servers, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Validate UI input against the stored servers, keeping stored keys where none was sent.</summary>
    public static (IReadOnlyList<ServerDefinition> Servers, IReadOnlyList<string> Errors) Merge(
        IReadOnlyList<ServerInput> input,
        IReadOnlyList<ServerDefinition> current)
    {
        var errors = new List<string>();
        var result = new List<ServerDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();

        for (var i = 0; i < input.Count; i++)
        {
            var item = input[i];
            var label = string.IsNullOrWhiteSpace(item.Name) ? $"Server #{i + 1}" : item.Name.Trim();
            var existing = item.Id is { } id ? current.FirstOrDefault(s => s.Id == id) : null;

            var definition = Normalize(item, existing, errors, label);
            if (definition is null)
                continue;
            if (!names.Add(definition.Name))
                errors.Add($"{label}: the name is used by more than one server.");
            if (!ids.Add(definition.Id))
                errors.Add($"{label}: duplicate server id.");
            result.Add(definition);
        }

        return (result, errors);
    }

    public static ServerDefinition? Normalize(ServerInput item, ServerDefinition? existing, List<string> errors, string label)
    {
        var errorCount = errors.Count;
        var name = item.Name?.Trim() ?? "";
        if (name.Length == 0)
            errors.Add($"{label}: a name is required.");

        var baseUrl = item.BaseUrl?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors.Add($"{label}: the URL must be an absolute http:// or https:// address.");

        var apiKey = item.ApiKey is null ? existing?.ApiKey ?? "" : item.ApiKey.Trim();
        if (apiKey.Length == 0)
            errors.Add($"{label}: an API key is required.");

        if (item.MaxConcurrency < 1 || item.MaxConcurrency > MaxConcurrencyLimit)
            errors.Add($"{label}: max concurrency must be between 1 and {MaxConcurrencyLimit}.");

        var mappings = new List<PathMapping>();
        foreach (var mapping in item.Mappings ?? [])
        {
            var cove = mapping.CovePrefix?.Trim() ?? "";
            var remote = mapping.RemotePrefix?.Trim() ?? "";
            if (cove.Length == 0 || remote.Length == 0)
            {
                errors.Add($"{label}: every path mapping needs both a Cove path and a server path.");
                continue;
            }
            mappings.Add(new PathMapping(cove, remote));
        }

        if (errors.Count > errorCount)
            return null;

        return new ServerDefinition
        {
            Id = item.Id ?? Guid.NewGuid(),
            Name = name,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Enabled = item.Enabled,
            MaxConcurrency = item.MaxConcurrency,
            Mappings = mappings,
        };
    }

    private async Task<IReadOnlyList<ServerDefinition>> LoadAsync(CancellationToken ct)
    {
        var json = await store.Store.GetAsync(StoreKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];
        return JsonSerializer.Deserialize<List<ServerDefinition>>(json, Json) ?? [];
    }
}
