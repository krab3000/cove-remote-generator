using System.Diagnostics;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IJobProgress = Cove.Core.Interfaces.IJobProgress;
using RemoteHeavylifter.Workers;

namespace RemoteHeavylifter.Generation;

/// <summary>Runs one remote generation job: pick workers, select videos, distribute, summarize.</summary>
public sealed class GenerationCoordinator(
    WorkerRegistry registry,
    WorkerHub hub,
    WorkerSettingsStore settingsStore,
    WorkerAccess access,
    string extensionId,
    IExtensionServiceScopeFactory scopes,
    CoveConfiguration config,
    CoordinatorTimings timings,
    TimeProvider time,
    ILogger<GenerationCoordinator> logger)
{
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Cancel every running generation (the extension is shutting down).</summary>
    public void CancelAll() => _shutdown.Cancel();

    public async Task RunAsync(GenerateRequest request, IJobProgress progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var token = linked.Token;
        var stopwatch = Stopwatch.StartNew();

        progress.Report(0, "Checking remote workers…");
        var (live, notes) = await PickWorkersAsync(request, token);
        if (live.Count == 0)
            throw new InvalidOperationException(notes.Count > 0
                ? $"No usable remote workers: {string.Join("; ", notes)}"
                : "No remote workers are connected.");
        foreach (var note in notes)
            logger.LogWarning("Remote generation: {Note}", note);

        var paths = new GeneratedPaths(config.GeneratedPath);
        var run = new RunContext(Guid.NewGuid().ToString("N")[..12], paths, PreviewSettings.From(config), request.Overwrite);

        progress.Report(0, "Selecting videos…");
        SelectionResult selection;
        await using (var scope = scopes.CreateAsyncScope())
        {
            selection = await scope.ServiceProvider.GetRequiredService<VideoWorkSelector>()
                .SelectAsync(request, paths, token);
        }

        var counters = new RunCounters { Total = selection.Work.Count + selection.Settled.Count };
        progress.DeclareUnitCount(counters.Total);
        foreach (var settled in selection.Settled)
        {
            using var unit = progress.StartUnit(settled.VideoId.ToString(System.Globalization.CultureInfo.InvariantCulture), settled.Label);
            unit.Complete(settled.Outcome, settled.Reason);
            if (settled.Outcome == JobUnitOutcome.Skipped)
                counters.Skipped++;
            else
                counters.Failed++;
        }

        if (selection.Work.Count == 0)
        {
            progress.SetSummary(counters.Total == 0
                ? $"Nothing to generate ({selection.Examined} matching videos already have the requested files)."
                : $"Nothing generated: {counters.Failed} failed, {counters.Skipped} skipped.");
            return;
        }

        logger.LogInformation("Remote generation {RunId}: {Count} videos on {Servers}",
            run.RunId, selection.Work.Count, string.Join(", ", live.Select(l => $"{l.Name}×{l.Slots}")));

        IReadOnlyDictionary<string, int> perWorker;
        try
        {
            var executor = new RunExecutor(hub, access, extensionId, timings, time, logger);
            perWorker = await executor.ExecuteAsync(live, selection.Work, run, progress, counters, token);
        }
        finally
        {
            TryDeleteDirectory(Path.Combine(paths.TempRoot, run.RunId));
        }

        progress.SetSummary(Summarize(counters, perWorker, stopwatch.Elapsed));
    }

    /// <summary>The enabled, requested workers that are connected and know how to reach Cove.</summary>
    internal async Task<(List<LiveWorker> Live, List<string> Notes)> PickWorkersAsync(GenerateRequest request, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var live = new List<LiveWorker>();
        var notes = new List<string>();
        foreach (var worker in await registry.GetAllAsync(ct))
        {
            if (!worker.Enabled || (request.WorkerIds.Count > 0 && !request.WorkerIds.Contains(worker.Id)))
                continue;
            if (hub.GetSession(worker.Id) is not { } session)
            {
                notes.Add($"{worker.Name} is not connected");
                continue;
            }
            if (WorkerHub.ResolveCoveBaseUrl(worker, settings, session.InboundBaseUrl) is not { } baseUrl)
            {
                notes.Add($"{worker.Name}: set the Cove URL for workers so it can read and upload files");
                continue;
            }
            live.Add(new LiveWorker(worker.Id, worker.Name, Math.Max(1, Math.Min(worker.MaxConcurrency, session.Info.Capacity)), baseUrl));
        }
        return (live, notes);
    }

    internal static string Summarize(RunCounters counters, IReadOnlyDictionary<string, int> perWorker, TimeSpan elapsed)
    {
        var text = $"Generated {counters.Succeeded} of {counters.Total} videos remotely";
        if (counters.Failed > 0)
            text += $", {counters.Failed} failed";
        if (counters.Skipped > 0)
            text += $", {counters.Skipped} skipped";
        text += $" in {(elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : $"{elapsed.Minutes}m {elapsed.Seconds}s")}";
        if (perWorker.Count > 0)
            text += " · " + string.Join(", ", perWorker.Select(p => $"{p.Key}: {p.Value}"));
        return text;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Scratch space only.
        }
    }
}
