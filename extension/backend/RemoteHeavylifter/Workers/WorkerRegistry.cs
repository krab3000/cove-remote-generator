using System.Text.Json;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Workers;

/// <summary>The configured workers, persisted as JSON in the extension store.</summary>
public sealed class WorkerRegistry(StoreHolder store)
{
    internal const string StoreKey = "workers";
    public const int MaxConcurrencyLimit = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<WorkerDefinition>? _cache;

    /// <summary>Raised after the list changes, so live sessions can be reconciled.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<WorkerDefinition>> GetAllAsync(CancellationToken ct = default)
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

    /// <summary>The enabled worker with this token hash, from the loaded list (call <see cref="GetAllAsync"/> once first).</summary>
    public WorkerDefinition? FindEnabledByHash(string tokenHash)
        => _cache?.FirstOrDefault(w => w.Enabled && WorkerTokens.HashEquals(w.TokenHash, tokenHash));

    public WorkerDefinition? FindByHash(string tokenHash)
        => _cache?.FirstOrDefault(w => WorkerTokens.HashEquals(w.TokenHash, tokenHash));

    /// <summary>Replace the whole list. Returns the validation errors, or none on success.</summary>
    public async Task<(IReadOnlyList<WorkerDefinition> Workers, IReadOnlyList<string> Errors)> ReplaceAsync(
        IReadOnlyList<WorkerInput> input,
        CancellationToken ct = default)
    {
        IReadOnlyList<WorkerDefinition> workers;
        await _gate.WaitAsync(ct);
        try
        {
            var current = _cache ?? await LoadAsync(ct);
            var (merged, errors) = Merge(input, current);
            if (errors.Count > 0)
                return (current, errors);

            await SaveAsync(merged, ct);
            workers = merged;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return (workers, []);
    }

    /// <summary>Trust a worker that dialed in: add it without a URL, so Cove waits for it to connect.</summary>
    public async Task<WorkerDefinition> AddTrustedAsync(PendingWorker pending, CancellationToken ct = default)
    {
        WorkerDefinition added;
        await _gate.WaitAsync(ct);
        try
        {
            var current = _cache ?? await LoadAsync(ct);
            if (current.FirstOrDefault(w => WorkerTokens.HashEquals(w.TokenHash, pending.TokenHash)) is { } existing)
                return existing;

            var name = UniqueName(string.IsNullOrWhiteSpace(pending.Name) ? $"worker-{pending.WorkerTokenId}" : pending.Name.Trim(), current);
            added = new WorkerDefinition { Id = Guid.NewGuid(), Name = name, TokenHash = pending.TokenHash };
            await SaveAsync([.. current, added], ct);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return added;
    }

    /// <summary>Validate UI input against the stored workers, keeping stored tokens where none was sent.</summary>
    public static (IReadOnlyList<WorkerDefinition> Workers, IReadOnlyList<string> Errors) Merge(
        IReadOnlyList<WorkerInput> input,
        IReadOnlyList<WorkerDefinition> current)
    {
        var errors = new List<string>();
        var result = new List<WorkerDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < input.Count; i++)
        {
            var item = input[i];
            var label = string.IsNullOrWhiteSpace(item.Name) ? $"Worker #{i + 1}" : item.Name.Trim();
            var existing = item.Id is { } id ? current.FirstOrDefault(w => w.Id == id) : null;

            var definition = Normalize(item, existing, errors, label);
            if (definition is null)
                continue;
            if (!names.Add(definition.Name))
                errors.Add($"{label}: the name is used by more than one worker.");
            if (!ids.Add(definition.Id))
                errors.Add($"{label}: duplicate worker id.");
            if (!hashes.Add(definition.TokenHash))
                errors.Add($"{label}: another worker already uses this token.");
            result.Add(definition);
        }

        return (result, errors);
    }

    public static WorkerDefinition? Normalize(WorkerInput item, WorkerDefinition? existing, List<string> errors, string label)
    {
        var errorCount = errors.Count;
        var name = item.Name?.Trim() ?? "";
        if (name.Length == 0)
            errors.Add($"{label}: a name is required.");

        // A sent token replaces the stored one; otherwise keep what is stored (possibly only the hash).
        var token = item.Token?.Trim();
        string? tokenHash;
        if (token is { Length: > 0 })
        {
            if (token.Length < WorkerTokens.MinLength)
                errors.Add($"{label}: the worker token must be at least {WorkerTokens.MinLength} characters.");
            tokenHash = WorkerTokens.Hash(token);
        }
        else
        {
            token = existing?.Token;
            tokenHash = existing?.TokenHash;
            if (tokenHash is null)
                errors.Add($"{label}: a worker token is required.");
        }

        var url = string.IsNullOrWhiteSpace(item.Url) ? null : item.Url.Trim().TrimEnd('/');
        if (url is not null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "ws" && uri.Scheme != "wss"))
                errors.Add($"{label}: the worker URL must be an absolute ws:// or wss:// address (or empty if the worker connects to Cove).");
            if (token is not { Length: > 0 })
                errors.Add($"{label}: Cove needs the worker token itself to connect to the worker URL; enter it again.");
        }

        var coveUrl = string.IsNullOrWhiteSpace(item.CoveUrlOverride) ? null : item.CoveUrlOverride.Trim().TrimEnd('/');
        if (coveUrl is not null && !IsHttpUrl(coveUrl))
            errors.Add($"{label}: the Cove URL override must be an absolute http:// or https:// address.");

        if (item.MaxConcurrency < 1 || item.MaxConcurrency > MaxConcurrencyLimit)
            errors.Add($"{label}: max parallel videos must be between 1 and {MaxConcurrencyLimit}.");

        if (errors.Count > errorCount)
            return null;

        return new WorkerDefinition
        {
            Id = item.Id ?? Guid.NewGuid(),
            Name = name,
            TokenHash = tokenHash!,
            // Without a URL Cove never dials, so it keeps only the hash.
            Token = url is null ? null : token,
            Url = url,
            CoveUrlOverride = coveUrl,
            Enabled = item.Enabled,
            MaxConcurrency = item.MaxConcurrency,
        };
    }

    public static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string UniqueName(string name, IReadOnlyList<WorkerDefinition> current)
    {
        var candidate = name;
        for (var n = 2; current.Any(w => string.Equals(w.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{name} ({n})";
        return candidate;
    }

    private async Task SaveAsync(IReadOnlyList<WorkerDefinition> workers, CancellationToken ct)
    {
        await store.Store.SetAsync(StoreKey, JsonSerializer.Serialize(workers, Json), ct);
        _cache = workers;
    }

    private async Task<IReadOnlyList<WorkerDefinition>> LoadAsync(CancellationToken ct)
    {
        var json = await store.Store.GetAsync(StoreKey, ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<WorkerDefinition>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
