namespace RemoteHeavylifter.Servers;

/// <summary>Cove path prefix → the same folder as the generation server sees it.</summary>
public sealed record PathMapping(string CovePrefix, string RemotePrefix);

/// <summary>A configured generation server, as stored (API key included — never sent to the browser).</summary>
public sealed record ServerDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public string ApiKey { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int MaxConcurrency { get; init; } = 2;
    public IReadOnlyList<PathMapping> Mappings { get; init; } = [];
}

/// <summary>A server as the UI sees it: the key is replaced by a hint.</summary>
public sealed record ServerView(
    Guid Id,
    string Name,
    string BaseUrl,
    bool HasApiKey,
    string? ApiKeyHint,
    bool Enabled,
    int MaxConcurrency,
    IReadOnlyList<PathMapping> Mappings)
{
    public static ServerView From(ServerDefinition server) => new(
        server.Id,
        server.Name,
        server.BaseUrl,
        server.ApiKey.Length > 0,
        server.ApiKey.Length >= 8 ? "…" + server.ApiKey[^4..] : server.ApiKey.Length > 0 ? "…" : null,
        server.Enabled,
        server.MaxConcurrency,
        server.Mappings);
}

/// <summary>A server as the UI submits it. A null <see cref="ApiKey"/> keeps the stored key.</summary>
public sealed record ServerInput(
    Guid? Id,
    string? Name,
    string? BaseUrl,
    string? ApiKey,
    bool Enabled = true,
    int MaxConcurrency = 2,
    IReadOnlyList<PathMapping>? Mappings = null);
