using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Workers;

namespace RemoteHeavylifter.Api;

public sealed record WorkerTestResult(bool Ok, string Message);

/// <summary>Settings as the UI sees them, plus whether Cove's auth is on (workers on public addresses need it).</summary>
public sealed record WorkerSettingsView(string? CoveUrlForWorkers, bool CoveAuthEnabled);

public sealed record ErrorBody(string Code, string Message, IReadOnlyList<string>? Errors = null);

/// <summary>The endpoints Cove's own UI calls (configured by people with the right Cove permissions).</summary>
internal static class RemoteEndpoints
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    public static void Map(IEndpointRouteBuilder endpoints, string extensionId)
    {
        var api = endpoints.MapGroup($"/api/ext/{extensionId}");

        api.MapGet("/workers", async (WorkerRegistry registry, CancellationToken ct) =>
                Results.Ok((await registry.GetAllAsync(ct)).Select(WorkerView.From)))
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapPut("/workers", async (List<WorkerInput> input, WorkerRegistry registry, CancellationToken ct) =>
            {
                var (workers, errors) = await registry.ReplaceAsync(input, ct);
                if (errors.Count > 0)
                    return Results.BadRequest(new ErrorBody("INVALID_WORKERS", "The worker list is not valid.", errors));
                return Results.Ok(workers.Select(WorkerView.From));
            })
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapGet("/workers/pending", async (PendingWorkers pending, WorkerRegistry registry, CancellationToken ct) =>
            {
                var configured = (await registry.GetAllAsync(ct)).Select(w => w.TokenHash).ToHashSet();
                return Results.Ok((await pending.GetAllAsync(ct))
                    .Where(p => !configured.Contains(p.TokenHash))
                    .Select(PendingWorkerView.From));
            })
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapPost("/workers/pending/{workerTokenId}/trust", async (string workerTokenId, PendingWorkers pending, WorkerRegistry registry, CancellationToken ct) =>
            {
                if (await pending.RemoveAsync(workerTokenId, ct) is not { } entry)
                    return Results.NotFound(new ErrorBody("NOT_PENDING", "No pending worker has that ID."));
                return Results.Ok(WorkerView.From(await registry.AddTrustedAsync(entry, ct)));
            })
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapDelete("/workers/pending/{workerTokenId}", async (string workerTokenId, PendingWorkers pending, CancellationToken ct) =>
            {
                await pending.RemoveAsync(workerTokenId, ct);
                return Results.NoContent();
            })
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapGet("/workers/health", async (bool? refresh, WorkerHub hub, CancellationToken ct) =>
                Results.Ok(await hub.GetHealthAsync(refresh ?? false, ct)))
            .RequireCovePermission(PermissionMode.Any, Permissions.JobsRun, Permissions.ExtensionsConfigure);

        api.MapPost("/workers/{id:guid}/test", TestWorkerAsync)
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapGet("/settings", async (WorkerSettingsStore settings, CoveConfiguration config, CancellationToken ct) =>
                Results.Ok(new WorkerSettingsView((await settings.GetAsync(ct)).CoveUrlForWorkers, config.Auth.Enabled)))
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapPut("/settings", async (WorkerSettingsView input, WorkerSettingsStore settings, CoveConfiguration config, CancellationToken ct) =>
            {
                var errors = await settings.SaveAsync(new WorkerSettings { CoveUrlForWorkers = input.CoveUrlForWorkers }, ct);
                if (errors.Count > 0)
                    return Results.BadRequest(new ErrorBody("INVALID_SETTINGS", errors[0], errors));
                return Results.Ok(new WorkerSettingsView((await settings.GetAsync(ct)).CoveUrlForWorkers, config.Auth.Enabled));
            })
            .RequireCovePermission(Permissions.ExtensionsConfigure);

        api.MapGet("/generate/options", async (GenerationOptionsStore options, CancellationToken ct) =>
                Results.Ok(await options.GetAsync(ct)))
            .RequireCovePermission(Permissions.JobsRun);

        // Whole-library generation writes into Cove's generated folder for every video, so it needs the
        // same standing as configuring the extension, not just the right to run jobs.
        api.MapPost("/generate", StartGenerationAsync)
            .RequireCovePermission(Permissions.JobsRun, Permissions.ExtensionsConfigure);
    }

    /// <summary>
    /// Checks the whole path a real task takes: the worker is connected, then it reads a real video from Cove over
    /// HTTP with its token (ffprobe on the source URL), which also proves the Cove URL it was given is reachable.
    /// </summary>
    private static async Task<IResult> TestWorkerAsync(
        Guid id,
        WorkerRegistry registry,
        WorkerHub hub,
        WorkerSettingsStore settingsStore,
        WorkerAccess access,
        IExtensionServiceScopeFactory scopes,
        CancellationToken ct)
    {
        if ((await registry.GetAllAsync(ct)).FirstOrDefault(w => w.Id == id) is not { } worker)
            return Results.NotFound(new ErrorBody("NOT_FOUND", "Save the worker first."));
        if (hub.GetSession(worker.Id) is not { } session)
        {
            var health = (await hub.GetHealthAsync(refresh: false, ct)).FirstOrDefault(h => h.Id == id);
            return Results.Ok(new WorkerTestResult(false, $"Not connected: {health?.Error ?? "unknown"}"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        try
        {
            var info = await session.RefreshInfoAsync(timeout.Token);
            var settings = await settingsStore.GetAsync(timeout.Token);
            if (WorkerHub.ResolveCoveBaseUrl(worker, settings, session.InboundBaseUrl) is not { } baseUrl)
                return Results.Ok(new WorkerTestResult(false, "Connected, but set the Cove URL for workers so it can read and upload files."));

            (string Path, long Size)? sample;
            await using (var scope = scopes.CreateAsyncScope())
                sample = await scope.ServiceProvider.GetRequiredService<VideoWorkSelector>().SampleSourceAsync(timeout.Token);
            if (sample is not { } source)
                return Results.Ok(new WorkerTestResult(true, $"Connected ({info.Encoder}, {info.Capacity} slots). No video available to test reading with."));

            var taskId = $"test-{Guid.NewGuid():N}";
            access.Register(new TaskAssignment(taskId, worker.Id, source.Path, [], Path.GetTempPath()));
            try
            {
                var url = $"{baseUrl}/api/ext/{RemoteHeavylifterExtension.ExtensionId}/worker/tasks/{taskId}/source";
                var probe = await session.ProbeAsync(new ProbeParams(taskId, url), timeout.Token);
                return Results.Ok(probe.Ok
                    ? new WorkerTestResult(true, $"Connected ({info.Encoder}, {info.Capacity} slots) and read a video from {baseUrl}.")
                    : new WorkerTestResult(false, $"Connected, but the worker could not read from {baseUrl}: {probe.Error}"));
            }
            finally
            {
                access.Remove(taskId);
            }
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            return Results.Ok(new WorkerTestResult(false, ex is OperationCanceledException ? "The worker did not answer in time." : ex.Message));
        }
    }

    private static async Task<IResult> StartGenerationAsync(
        GenerateRequest request,
        HttpContext http,
        WorkerHub hub,
        GenerationOptionsStore options,
        GenerationCoordinator coordinator,
        IJobService jobs,
        CancellationToken ct)
    {
        if (!request.AnyArtifact)
            return Results.BadRequest(new ErrorBody("NO_ARTIFACTS", "Select at least one of covers, previews or sprites."));
        if (request.Sprite && !SpriteSettings.IsValidWidth(request.SpriteWidth))
        {
            return Results.BadRequest(new ErrorBody("INVALID_SPRITE_WIDTH",
                $"Sprite tile width must be between {SpriteSettings.MinWidth} and {SpriteSettings.MaxWidth} px."));
        }
        if (request.WorkerIds.Count == 0)
            return Results.BadRequest(new ErrorBody("NO_WORKERS", "Select at least one worker."));

        var normalized = request with { Paths = PathFilter.Normalize(request.Paths) };
        var (live, notes) = await coordinator.PickWorkersAsync(normalized, ct);
        if (live.Count == 0)
        {
            return Results.Json(
                new ErrorBody("NO_LIVE_WORKERS", notes.Count > 0 ? string.Join("; ", notes) : "None of the selected workers is connected right now."),
                statusCode: StatusCodes.Status409Conflict);
        }

        await options.SaveAsync(normalized, ct);
        var accepted = normalized with { WorkerIds = live.Select(w => w.WorkerId).ToList() };

        var owner = JobOwner.FromPrincipal(http.RequestServices.GetService<ICurrentPrincipalAccessor>()?.Current);
        var description = $"Remote generation on {string.Join(", ", live.Select(w => w.Name))}";
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
