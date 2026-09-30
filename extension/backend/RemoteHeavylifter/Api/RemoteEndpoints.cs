using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RemoteHeavylifter.Contract;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Remote;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Api;

public sealed record ServerHealthView(
    Guid Id,
    string Name,
    bool Enabled,
    string State,
    bool Live,
    long? LatencyMs,
    string? ServerVersion,
    string? FfmpegVersion,
    string? Encoder,
    int? Capacity,
    int? Running,
    int? Queued,
    long? DiskFreeBytes,
    string? Error,
    DateTimeOffset CheckedAt)
{
    public static ServerHealthView From(ServerHealth health) => new(
        health.ServerId, health.Name, health.Enabled, health.State, health.Live, health.LatencyMs,
        health.Info?.ServerVersion, health.Info?.FfmpegVersion, health.Info?.Encoder, health.Info?.Capacity,
        health.Info?.Running, health.Info?.Queued, health.Info?.DiskFreeBytes, health.Error, health.CheckedAt);
}

public sealed record MappingSample(string CovePath, string? RemotePath, bool Allowed, bool Exists, bool Readable);

public sealed record MappingCheck(string CovePrefix, string RemotePrefix, IReadOnlyList<MappingSample> Samples, string? Error);

public sealed record ServerTestResult(ServerHealthView Health, IReadOnlyList<MappingCheck> Mappings, IReadOnlyList<string> MediaRoots);

public sealed record ErrorBody(string Code, string Message, IReadOnlyList<string>? Errors = null);

internal static class RemoteEndpoints
{
    private const int SamplesPerMapping = 5;

    public static void Map(IEndpointRouteBuilder endpoints, string extensionId)
    {
        var api = endpoints.MapGroup($"/api/ext/{extensionId}");

        api.MapGet("/servers", async (ServerRegistry registry, CancellationToken ct) =>
                Results.Ok((await registry.GetAllAsync(ct)).Select(ServerView.From)))
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapPut("/servers", async (List<ServerInput> input, ServerRegistry registry, HealthMonitor health, CancellationToken ct) =>
            {
                var (servers, errors) = await registry.ReplaceAsync(input, ct);
                if (errors.Count > 0)
                    return Results.BadRequest(new ErrorBody("INVALID_SERVERS", "The server list is not valid.", errors));
                health.Invalidate();
                return Results.Ok(servers.Select(ServerView.From));
            })
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapPost("/servers/test", TestServerAsync)
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapGet("/servers/health", async (bool? refresh, ServerRegistry registry, HealthMonitor health, CancellationToken ct) =>
            {
                var servers = await registry.GetAllAsync(ct);
                var probes = await health.ProbeAsync(servers, refresh ?? false, ct);
                return Results.Ok(probes.Select(ServerHealthView.From));
            })
            .RequireCovePermission(PermissionMode.Any, Permissions.JobsRun, Permissions.ExtensionsConfigure);

        api.MapGet("/generate/options", async (GenerationOptionsStore options, CancellationToken ct) =>
                Results.Ok(await options.GetAsync(ct)))
            .RequireCovePermission(Permissions.JobsRun);

        // Whole-library generation writes into Cove's generated folder for every video, so it needs the
        // same standing as configuring the extension, not just the right to run jobs.
        api.MapPost("/generate", StartGenerationAsync)
            .RequireCovePermission(Permissions.JobsRun, Permissions.ExtensionsConfigure);
    }

    private static async Task<IResult> TestServerAsync(
        ServerInput input,
        ServerRegistry registry,
        HealthMonitor health,
        IRemoteClientFactory clients,
        IExtensionServiceScopeFactory scopes,
        CancellationToken ct)
    {
        var stored = input.Id is { } id ? (await registry.GetAllAsync(ct)).FirstOrDefault(s => s.Id == id) : null;
        var errors = new List<string>();
        var server = ServerRegistry.Normalize(input, stored, errors, input.Name ?? "Server");
        if (server is null)
            return Results.BadRequest(new ErrorBody("INVALID_SERVER", "The server settings are not valid.", errors));

        var probe = await health.ProbeAsync(server, refresh: true, ct, includeDisabled: true);
        var view = ServerHealthView.From(probe);
        if (!probe.Live)
            return Results.Ok(new ServerTestResult(view, [], []));

        var client = clients.Create(server);
        var checks = new List<MappingCheck>();
        await using var scope = scopes.CreateAsyncScope();
        var selector = scope.ServiceProvider.GetRequiredService<VideoWorkSelector>();
        foreach (var mapping in server.Mappings)
        {
            try
            {
                var covePaths = await selector.SamplePathsAsync(mapping.CovePrefix, SamplesPerMapping, ct);
                if (covePaths.Count == 0)
                {
                    checks.Add(new MappingCheck(mapping.CovePrefix, mapping.RemotePrefix, [], "No videos in Cove's library are under this path."));
                    continue;
                }

                var mapped = covePaths.Select(p => (Cove: p, Remote: PathMapper.Map(p, [mapping]))).ToList();
                var results = await client.CheckPathsAsync(mapped.Where(m => m.Remote != null).Select(m => m.Remote!).ToList(), ct);
                var byPath = results.GroupBy(r => r.Path).ToDictionary(g => g.Key, g => g.First());
                checks.Add(new MappingCheck(mapping.CovePrefix, mapping.RemotePrefix,
                    mapped.Select(m => m.Remote is { } remote && byPath.TryGetValue(remote, out var r)
                        ? new MappingSample(m.Cove, remote, r.Allowed, r.Exists, r.Readable)
                        : new MappingSample(m.Cove, m.Remote, false, false, false)).ToList(),
                    null));
            }
            catch (RemoteException ex)
            {
                checks.Add(new MappingCheck(mapping.CovePrefix, mapping.RemotePrefix, [], ex.Message));
            }
        }

        return Results.Ok(new ServerTestResult(view, checks, probe.Info?.MediaRoots ?? []));
    }

    private static async Task<IResult> StartGenerationAsync(
        GenerateRequest request,
        HttpContext http,
        ServerRegistry registry,
        HealthMonitor health,
        GenerationOptionsStore options,
        GenerationCoordinator coordinator,
        IJobService jobs,
        CancellationToken ct)
    {
        if (!request.AnyArtifact)
            return Results.BadRequest(new ErrorBody("NO_ARTIFACTS", "Select at least one of covers, previews or sprites."));
        if (request.ServerIds.Count == 0)
            return Results.BadRequest(new ErrorBody("NO_SERVERS", "Select at least one generation server."));

        var selected = (await registry.GetAllAsync(ct))
            .Where(s => s.Enabled && request.ServerIds.Contains(s.Id))
            .ToList();
        var probes = await health.ProbeAsync(selected, refresh: true, ct);
        var live = probes.Where(p => p.Live).ToList();
        if (live.Count == 0)
        {
            return Results.Json(
                new ErrorBody("NO_LIVE_SERVERS", "None of the selected generation servers is reachable right now."),
                statusCode: StatusCodes.Status409Conflict);
        }

        var accepted = request with
        {
            ServerIds = live.Select(p => p.ServerId).ToList(),
            Paths = PathFilter.Normalize(request.Paths),
        };
        await options.SaveAsync(request with { Paths = accepted.Paths }, ct);

        var owner = JobOwner.FromPrincipal(http.RequestServices.GetService<ICurrentPrincipalAccessor>()?.Current);
        var description = $"Remote generation on {string.Join(", ", live.Select(p => p.Name))}";
        var jobId = jobs.EnqueueFor(
            owner,
            $"ext:{RemoteHeavylifterExtension.ExtensionId}:generate",
            description,
            (progress, token) => coordinator.RunAsync(accepted, progress, token),
            resultUrl: null,
            exclusive: false);

        return Results.Accepted(value: new { jobId });
    }
}
