using System.Globalization;
using System.Threading.Channels;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Contract;
using RemoteHeavylifter.Remote;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Generation;

public sealed record CoordinatorTimings(
    IReadOnlyList<TimeSpan> PollDelays,
    TimeSpan ReviveInterval,
    TimeSpan AllDeadGrace,
    TimeSpan DeferDelay,
    TimeSpan QueuedTimeout,
    TimeSpan RunningTimeout,
    TimeSpan CleanupTimeout,
    int MaxAttempts,
    int FailuresBeforeDead,
    int PollErrorsBeforeGivingUp)
{
    public static CoordinatorTimings Default { get; } = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)],
        ReviveInterval: TimeSpan.FromSeconds(30),
        AllDeadGrace: TimeSpan.FromMinutes(2),
        DeferDelay: TimeSpan.FromMilliseconds(500),
        QueuedTimeout: TimeSpan.FromHours(1),
        RunningTimeout: TimeSpan.FromMinutes(20),
        CleanupTimeout: TimeSpan.FromSeconds(5),
        MaxAttempts: 3,
        FailuresBeforeDead: 3,
        PollErrorsBeforeGivingUp: 3);
}

/// <summary>A server taking part in a run, with how many videos it may work on at once.</summary>
public sealed record LiveServer(ServerDefinition Server, int Slots);

public sealed record RunContext(string RunId, GeneratedPaths Paths, PreviewSettings Preview, bool Overwrite);

/// <summary>Unit totals across a run, including videos settled before any remote work.</summary>
public sealed class RunCounters
{
    public int Total;
    public int Succeeded;
    public int Failed;
    public int Skipped;

    public int Done => Volatile.Read(ref Succeeded) + Volatile.Read(ref Failed) + Volatile.Read(ref Skipped);
}

/// <summary>
/// Spreads one run's videos over the live servers. Each server gets as many worker loops as it has
/// slots; a worker submits one video, polls it, downloads and commits the artifacts, then deletes the
/// remote task. A server that keeps failing is marked dead: its workers stop and whatever it held goes
/// back on the queue for the others, and a reviver brings it back if it recovers.
/// </summary>
internal sealed class RunExecutor(
    IRemoteClientFactory clients,
    HealthMonitor health,
    CoordinatorTimings timings,
    TimeProvider time,
    ILogger logger)
{
    private sealed class Slot(LiveServer live, IRemoteClient client)
    {
        public LiveServer Live { get; } = live;
        public IRemoteClient Client { get; } = client;
        public ServerDefinition Server => Live.Server;
        public volatile bool Dead;
        public DateTimeOffset DeadSince;
        public string? LastError;
        public int ConsecutiveFailures;
        public int ActiveWorkers;
        public int Succeeded;
        public int Failed;
    }

    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();
    private readonly TaskCompletionSource _allDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _workers = [];
    private List<Slot> _slots = [];
    private RunContext _run = null!;
    private IJobProgress _progress = null!;
    private RunCounters _counters = null!;
    private CancellationToken _token;
    private int _remaining;

    public async Task<IReadOnlyDictionary<string, int>> ExecuteAsync(
        IReadOnlyList<LiveServer> servers,
        IReadOnlyList<WorkItem> items,
        RunContext run,
        IJobProgress progress,
        RunCounters counters,
        CancellationToken ct)
    {
        _run = run;
        _progress = progress;
        _counters = counters;
        _remaining = items.Count;
        if (items.Count == 0)
            return new Dictionary<string, int>();

        foreach (var item in items)
            _queue.Writer.TryWrite(item);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _token = runCts.Token;
        _slots = servers.Select(s => new Slot(s, clients.Create(s.Server))).ToList();
        foreach (var slot in _slots)
            StartWorkers(slot);
        var reviver = Task.Run(ReviveLoopAsync, CancellationToken.None);

        try
        {
            await _allDone.Task.WaitAsync(ct);
        }
        finally
        {
            await runCts.CancelAsync();
            Task[] all;
            lock (_workers)
                all = [.. _workers, reviver];
            await Task.WhenAll(all).ContinueWith(static _ => { }, TaskScheduler.Default);
        }

        return _slots.ToDictionary(s => s.Server.Name, s => s.Succeeded);
    }

    // ---- workers ----------------------------------------------------------------------------

    private void StartWorkers(Slot slot)
    {
        var missing = slot.Live.Slots - Volatile.Read(ref slot.ActiveWorkers);
        for (var i = 0; i < missing; i++)
        {
            Interlocked.Increment(ref slot.ActiveWorkers);
            lock (_workers)
                _workers.Add(Task.Run(() => WorkerAsync(slot), CancellationToken.None));
        }
    }

    private async Task WorkerAsync(Slot slot)
    {
        try
        {
            while (!slot.Dead && await _queue.Reader.WaitToReadAsync(_token))
            {
                if (slot.Dead || !_queue.Reader.TryRead(out var item))
                    continue;
                if (item.RemotePaths.TryGetValue(slot.Server.Id, out var remotePath))
                    await ProcessAsync(slot, item, remotePath);
                else
                    await DeferAsync(item);
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Remote generation worker for {Server} stopped unexpectedly", slot.Server.Name);
        }
        finally
        {
            Interlocked.Decrement(ref slot.ActiveWorkers);
        }
    }

    /// <summary>This server cannot reach the item's file; leave it for one that can.</summary>
    private async Task DeferAsync(WorkItem item)
    {
        var candidates = _slots.Where(s => item.RemotePaths.ContainsKey(s.Server.Id)).ToList();
        var now = time.GetUtcNow();
        if (candidates.Count == 0 || candidates.All(s => s.Dead && now - s.DeadSince >= timings.AllDeadGrace))
        {
            var notes = item.Notes.Count > 0 ? string.Join("; ", item.Notes) : "no live server can reach the file";
            Finish(item, null, JobUnitOutcome.Failed, notes);
            return;
        }

        await Task.Delay(timings.DeferDelay, time, _token);
        _queue.Writer.TryWrite(item);
    }

    private async Task ProcessAsync(Slot slot, WorkItem item, string remotePath)
    {
        item.Unit ??= _progress.StartUnit(item.VideoId.ToString(CultureInfo.InvariantCulture), item.Label);
        item.Attempts++;
        var clientTaskId = $"cove-{_run.RunId}-{item.VideoId}-a{item.Attempts}";
        item.Unit.Report(0, $"{slot.Server.Name}: submitting");
        string? taskId = null;

        try
        {
            var status = await slot.Client.SubmitAsync(BuildRequest(item, remotePath, clientTaskId), _token);
            taskId = status.TaskId;
            status = await PollAsync(slot, item, status);
            Interlocked.Exchange(ref slot.ConsecutiveFailures, 0);

            if (status.Status is RemoteTaskStates.Failed or RemoteTaskStates.Cancelled)
            {
                switch (status.ErrorCode)
                {
                    case "server_restarted":
                        throw new RemoteException($"{slot.Server.Name} restarted while generating", transient: true);
                    case "source_not_found":
                        ExcludeServer(slot, item, status.Error ?? "source file not found");
                        return;
                    default:
                        Finish(item, slot, JobUnitOutcome.Failed, $"{slot.Server.Name}: {status.Error ?? "generation failed"}");
                        return;
                }
            }

            var errors = await CollectAsync(slot, item, status);
            Finish(item, slot,
                errors.Count == 0 ? JobUnitOutcome.Succeeded : JobUnitOutcome.Failed,
                errors.Count == 0 ? null : $"{slot.Server.Name}: {string.Join("; ", errors)}");
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            throw;
        }
        catch (RemoteException ex) when (ex.Code == "path_not_allowed")
        {
            ExcludeServer(slot, item, "the mapped path is outside the server's media roots");
        }
        catch (RemoteException ex) when (ex.IsAuthFailure)
        {
            MarkDead(slot, "the server rejected the API key");
            item.Attempts--;
            _queue.Writer.TryWrite(item);
        }
        catch (RemoteException ex) when (ex.Transient)
        {
            OnTransientFailure(slot, item, ex);
        }
        catch (Exception ex)
        {
            Finish(item, slot, JobUnitOutcome.Failed, $"{slot.Server.Name}: {ex.Message}");
        }
        finally
        {
            if (taskId is not null)
                await DeleteQuietlyAsync(slot, taskId);
        }
    }

    private RemoteTaskRequest BuildRequest(WorkItem item, string remotePath, string clientTaskId)
    {
        var preview = _run.Preview;
        return new RemoteTaskRequest(
            clientTaskId,
            item.VideoId,
            remotePath,
            item.Duration,
            item.Cover ? new CoverSpec(item.CoverSeek, item.CoverFilter) : null,
            item.Preview
                ? new PreviewSpec(preview.Segments, preview.SegmentDuration, preview.ExcludeStart, preview.ExcludeEnd,
                    preview.Preset, preview.Audio, PreviewSettings.Crf, PreviewSettings.Width, item.PreviewScale)
                : null,
            item.Sprite ? new SpriteSpec(81, 160, item.SpriteFilter, GeneratedPaths.SpriteFileName(item.VideoId)) : null);
    }

    private async Task<RemoteTaskStatus> PollAsync(Slot slot, WorkItem item, RemoteTaskStatus status)
    {
        var submittedAt = time.GetUtcNow();
        DateTimeOffset? runningSince = status.Status == RemoteTaskStates.Running ? submittedAt : null;
        var runningTimeout = timings.RunningTimeout + TimeSpan.FromSeconds(Math.Max(0, item.Duration) / 4);
        var poll = 0;
        var errors = 0;

        while (!status.IsTerminal)
        {
            var now = time.GetUtcNow();
            if (runningSince is null && now - submittedAt > timings.QueuedTimeout)
                throw new RemoteException($"{slot.Server.Name}: waited too long in the server's queue", transient: true);
            if (runningSince is { } started && now - started > runningTimeout)
                throw new RemoteException($"{slot.Server.Name}: did not finish within {runningTimeout.TotalMinutes:0} minutes", transient: true);

            await Task.Delay(timings.PollDelays[Math.Min(poll++, timings.PollDelays.Count - 1)], time, _token);

            RemoteTaskStatus? next;
            try
            {
                next = await slot.Client.GetTaskAsync(status.TaskId, _token);
                errors = 0;
            }
            catch (RemoteException ex) when (ex.Transient)
            {
                if (++errors >= timings.PollErrorsBeforeGivingUp)
                    throw;
                continue;
            }

            status = next ?? throw new RemoteException($"{slot.Server.Name} no longer knows the task (restarted?)", transient: true);
            if (status.Status == RemoteTaskStates.Running && runningSince is null)
                runningSince = time.GetUtcNow();
            item.Unit?.Report(status.Progress, $"{slot.Server.Name}: {status.Status}");
        }

        return status;
    }

    /// <summary>Download and commit every requested artifact the server produced. Returns what is missing.</summary>
    private async Task<List<string>> CollectAsync(Slot slot, WorkItem item, RemoteTaskStatus status)
    {
        var errors = new List<string>();
        var paths = _run.Paths;
        var id = item.VideoId;
        var dir = Path.Combine(paths.TempRoot, _run.RunId, id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            if (item.Cover)
            {
                if (Artifact(status, ArtifactKinds.Cover) is { Succeeded: true } cover)
                {
                    var file = await DownloadAsync(slot, status.TaskId, ArtifactKinds.Cover, cover, Path.Combine(dir, "cover.jpg"));
                    ArtifactCommitter.CommitSingle(file, paths.Cover(id), _run.Overwrite);
                }
                else
                {
                    errors.Add(Describe(ArtifactKinds.Cover, Artifact(status, ArtifactKinds.Cover)));
                }
            }

            if (item.Preview)
            {
                if (Artifact(status, ArtifactKinds.Preview) is { Succeeded: true } preview)
                {
                    var file = await DownloadAsync(slot, status.TaskId, ArtifactKinds.Preview, preview, Path.Combine(dir, "preview.mp4"));
                    ArtifactCommitter.CommitSingle(file, paths.Preview(id), _run.Overwrite);
                }
                else
                {
                    errors.Add(Describe(ArtifactKinds.Preview, Artifact(status, ArtifactKinds.Preview)));
                }
            }

            if (item.Sprite)
            {
                var sprite = Artifact(status, ArtifactKinds.Sprite);
                var vtt = Artifact(status, ArtifactKinds.Vtt);
                if (sprite is { Succeeded: true } && vtt is { Succeeded: true })
                {
                    var spriteFile = await DownloadAsync(slot, status.TaskId, ArtifactKinds.Sprite, sprite, Path.Combine(dir, "sprite.jpg"));
                    var vttFile = await DownloadAsync(slot, status.TaskId, ArtifactKinds.Vtt, vtt, Path.Combine(dir, "thumbs.vtt"));
                    ArtifactCommitter.CommitSpritePair(spriteFile, vttFile, paths.Sprite(id), paths.SpriteVtt(id), _run.Overwrite);
                }
                else
                {
                    errors.Add(Describe(ArtifactKinds.Sprite, sprite is { Succeeded: true } ? vtt : sprite));
                }
            }
        }
        finally
        {
            TryDeleteDirectory(dir);
        }

        return errors;
    }

    private async Task<string> DownloadAsync(Slot slot, string taskId, string kind, RemoteArtifact artifact, string destination)
    {
        var result = await slot.Client.DownloadArtifactAsync(taskId, kind, destination, _token);
        if (artifact.Size is { } size && size != result.Length)
            throw new RemoteException($"{kind} download was truncated ({result.Length} of {size} bytes)", transient: true);
        if (artifact.Sha256 is { Length: > 0 } sha && !string.Equals(sha, result.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new RemoteException($"{kind} download failed its checksum", transient: true);
        return destination;
    }

    private static RemoteArtifact? Artifact(RemoteTaskStatus status, string kind)
        => status.Artifacts.TryGetValue(kind, out var artifact) ? artifact : null;

    private static string Describe(string kind, RemoteArtifact? artifact)
        => $"{kind} failed{(artifact?.Error is { Length: > 0 } error ? $" ({error})" : "")}";

    // ---- outcomes ---------------------------------------------------------------------------

    private void ExcludeServer(Slot slot, WorkItem item, string reason)
    {
        item.RemotePaths.Remove(slot.Server.Id);
        item.Notes.Add($"{slot.Server.Name}: {reason}");
        if (item.RemotePaths.Count == 0)
            Finish(item, null, JobUnitOutcome.Failed, string.Join("; ", item.Notes));
        else
            _queue.Writer.TryWrite(item);
    }

    private void OnTransientFailure(Slot slot, WorkItem item, RemoteException ex)
    {
        var failures = Interlocked.Increment(ref slot.ConsecutiveFailures);
        logger.LogWarning("Remote generation of video {VideoId} on {Server} failed (attempt {Attempt}): {Error}",
            item.VideoId, slot.Server.Name, item.Attempts, ex.Message);
        if (failures >= timings.FailuresBeforeDead)
            MarkDead(slot, ex.Message);

        if (item.Attempts >= timings.MaxAttempts)
        {
            Finish(item, slot, JobUnitOutcome.Failed, $"Gave up after {item.Attempts} attempts: {ex.Message}");
            return;
        }

        item.Unit?.Report(0, "Waiting to retry");
        _queue.Writer.TryWrite(item);
    }

    private void MarkDead(Slot slot, string reason)
    {
        lock (slot)
        {
            if (slot.Dead)
                return;
            slot.Dead = true;
            slot.DeadSince = time.GetUtcNow();
            slot.LastError = reason;
        }
        logger.LogWarning("Remote generation server {Server} taken out of the run: {Reason}", slot.Server.Name, reason);
    }

    private void Finish(WorkItem item, Slot? slot, JobUnitOutcome outcome, string? message)
    {
        var unit = item.Unit ??= _progress.StartUnit(item.VideoId.ToString(CultureInfo.InvariantCulture), item.Label);
        unit.Complete(outcome, message);
        unit.Dispose();

        switch (outcome)
        {
            case JobUnitOutcome.Succeeded:
                Interlocked.Increment(ref _counters.Succeeded);
                if (slot is not null) Interlocked.Increment(ref slot.Succeeded);
                break;
            case JobUnitOutcome.Failed:
                Interlocked.Increment(ref _counters.Failed);
                if (slot is not null) Interlocked.Increment(ref slot.Failed);
                break;
            default:
                Interlocked.Increment(ref _counters.Skipped);
                break;
        }

        var live = _slots.Count(s => !s.Dead);
        _progress.Report(
            _counters.Total == 0 ? 1 : (double)_counters.Done / _counters.Total,
            $"{_counters.Done} / {_counters.Total} videos · {Volatile.Read(ref _counters.Failed)} failed · "
            + $"{Volatile.Read(ref _counters.Skipped)} skipped · {live} server{(live == 1 ? "" : "s")} live");

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _queue.Writer.TryComplete();
            _allDone.TrySetResult();
        }
    }

    // ---- server recovery --------------------------------------------------------------------

    private async Task ReviveLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(timings.ReviveInterval, time, _token);

                foreach (var slot in _slots.Where(s => s.Dead))
                {
                    var probe = await health.ProbeAsync(slot.Server, refresh: true, _token);
                    if (!probe.Live)
                        continue;
                    lock (slot)
                    {
                        slot.Dead = false;
                        slot.ConsecutiveFailures = 0;
                    }
                    logger.LogInformation("Remote generation server {Server} is back; resuming work on it", slot.Server.Name);
                    StartWorkers(slot);
                }

                var now = time.GetUtcNow();
                if (_slots.All(s => s.Dead && Volatile.Read(ref s.ActiveWorkers) == 0 && now - s.DeadSince >= timings.AllDeadGrace))
                {
                    var reasons = string.Join("; ", _slots.Select(s => $"{s.Server.Name}: {s.LastError}"));
                    while (_queue.Reader.TryRead(out var item))
                        Finish(item, null, JobUnitOutcome.Failed, $"No live remote generation servers ({reasons})");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Remote generation reviver stopped unexpectedly");
        }
    }

    // ---- cleanup ----------------------------------------------------------------------------

    private async Task DeleteQuietlyAsync(Slot slot, string taskId)
    {
        using var cts = new CancellationTokenSource(timings.CleanupTimeout);
        try
        {
            await slot.Client.DeleteTaskAsync(taskId, cts.Token);
        }
        catch (Exception ex)
        {
            // The server's janitor removes it after its TTL anyway.
            logger.LogDebug(ex, "Could not delete remote task {TaskId} on {Server}", taskId, slot.Server.Name);
        }
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
            // Scratch space; the run directory is removed at the end of the run as well.
        }
    }
}
