using System.Diagnostics;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Remote;
using IJobProgress = Cove.Core.Interfaces.IJobProgress;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Generation;

/// <summary>Runs one remote generation job: pick servers, select videos, distribute, summarize.</summary>
public sealed class GenerationCoordinator(
    ServerRegistry registry,
    HealthMonitor health,
    IRemoteClientFactory clients,
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

        progress.Report(0, "Checking remote servers…");
        var chosen = (await registry.GetAllAsync(token))
            .Where(s => s.Enabled && (request.ServerIds.Count == 0 || request.ServerIds.Contains(s.Id)))
            .ToList();
        var probes = await health.ProbeAsync(chosen, refresh: true, token);
        var live = chosen.Zip(probes)
            .Where(pair => pair.Second.Live)
            .Select(pair => new LiveServer(pair.First, Math.Max(1, Math.Min(pair.First.MaxConcurrency, pair.Second.Info!.Capacity))))
            .ToList();
        if (live.Count == 0)
            throw new InvalidOperationException("No live remote generation servers are available.");

        var paths = new GeneratedPaths(config.GeneratedPath);
        var run = new RunContext(Guid.NewGuid().ToString("N")[..12], paths, PreviewSettings.From(config), request.Overwrite);

        progress.Report(0, "Selecting videos…");
        SelectionResult selection;
        await using (var scope = scopes.CreateAsyncScope())
        {
            selection = await scope.ServiceProvider.GetRequiredService<VideoWorkSelector>()
                .SelectAsync(request, paths, live.Select(l => l.Server).ToList(), token);
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
            run.RunId, selection.Work.Count, string.Join(", ", live.Select(l => $"{l.Server.Name}×{l.Slots}")));

        IReadOnlyDictionary<string, int> perServer;
        try
        {
            var executor = new RunExecutor(clients, health, timings, time, logger);
            perServer = await executor.ExecuteAsync(live, selection.Work, run, progress, counters, token);
        }
        finally
        {
            TryDeleteDirectory(Path.Combine(paths.TempRoot, run.RunId));
        }

        progress.SetSummary(Summarize(counters, perServer, stopwatch.Elapsed));
    }

    internal static string Summarize(RunCounters counters, IReadOnlyDictionary<string, int> perServer, TimeSpan elapsed)
    {
        var text = $"Generated {counters.Succeeded} of {counters.Total} videos remotely";
        if (counters.Failed > 0)
            text += $", {counters.Failed} failed";
        if (counters.Skipped > 0)
            text += $", {counters.Skipped} skipped";
        text += $" in {(elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : $"{elapsed.Minutes}m {elapsed.Seconds}s")}";
        if (perServer.Count > 0)
            text += " · " + string.Join(", ", perServer.Select(p => $"{p.Key}: {p.Value}"));
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
