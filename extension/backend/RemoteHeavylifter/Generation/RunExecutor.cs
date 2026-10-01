using System.Globalization;
using System.Threading.Channels;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Workers;

namespace RemoteHeavylifter.Generation;

public sealed record CoordinatorTimings(
    TimeSpan ReviveInterval,
    TimeSpan AllDeadGrace,
    TimeSpan RunningTimeout,
    int MaxAttempts,
    int FailuresBeforeDead)
{
    public static CoordinatorTimings Default { get; } = new(
        ReviveInterval: TimeSpan.FromSeconds(5),
        AllDeadGrace: TimeSpan.FromMinutes(2),
        RunningTimeout: TimeSpan.FromMinutes(20),
        MaxAttempts: 3,
        FailuresBeforeDead: 3);
}

/// <summary>A worker taking part in a run: how many videos it may work on at once, and how it reaches Cove.</summary>
public sealed record LiveWorker(Guid WorkerId, string Name, int Slots, string CoveBaseUrl);

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
/// Spreads one run's videos over the live workers. Each worker gets as many loops as it has slots; a loop
/// submits one video over the worker's session and waits for it to finish — the worker reads the source and
/// uploads the artifacts through the extension's HTTP endpoints — then commits what was uploaded. A worker
/// that keeps failing or disconnects is marked dead: its loops stop and whatever it held goes back on the
/// queue for the others, and a reviver brings it back when its session reconnects.
/// </summary>
internal sealed class RunExecutor(
    IWorkerDirectory workers,
    WorkerAccess access,
    IFingerprintStore fingerprints,
    string extensionId,
    CoordinatorTimings timings,
    TimeProvider time,
    ILogger logger)
{
    private sealed class Slot(LiveWorker live)
    {
        public LiveWorker Live { get; } = live;
        public string Name => Live.Name;
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
        IReadOnlyList<LiveWorker> live,
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
        _slots = live.Select(w => new Slot(w)).ToList();
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

        return _slots.ToDictionary(s => s.Name, s => s.Succeeded);
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
                await ProcessAsync(slot, item);
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Remote generation loop for {Worker} stopped unexpectedly", slot.Name);
        }
        finally
        {
            Interlocked.Decrement(ref slot.ActiveWorkers);
        }
    }

    private async Task ProcessAsync(Slot slot, WorkItem item)
    {
        if (workers.GetSession(slot.Live.WorkerId) is not { } session)
        {
            // The connection dropped between runs of this loop; hand the item to someone else.
            MarkDead(slot, "disconnected");
            _queue.Writer.TryWrite(item);
            return;
        }

        item.Unit ??= _progress.StartUnit(item.VideoId.ToString(CultureInfo.InvariantCulture), item.Label);
        item.Attempts++;
        var taskId = $"{_run.RunId}-{item.VideoId}-a{item.Attempts}";
        var uploadDir = Path.Combine(_run.Paths.TempRoot, _run.RunId, item.VideoId.ToString(CultureInfo.InvariantCulture), $"a{item.Attempts}");
        var assignment = new TaskAssignment(taskId, slot.Live.WorkerId, item.SourcePath, RequestedKinds(item), uploadDir);
        access.Register(assignment);
        item.Unit.Report(0, $"{slot.Name}: submitting");

        var runningTimeout = timings.RunningTimeout + TimeSpan.FromSeconds(Math.Max(0, item.Duration) / 4);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
        timeout.CancelAfter(runningTimeout);
        try
        {
            var result = await session.RunTaskAsync(
                BuildRequest(item, taskId, slot.Live.CoveBaseUrl),
                p => item.Unit?.Report(p.Progress, $"{slot.Name}: {p.Stage ?? "working"}"),
                timeout.Token);

            // A worker that cannot reach Cove is a problem with that worker, not the video: count it against
            // the worker (so it is taken out after a few) and let another one try.
            if (result.ErrorCode == ErrorCodes.SourceUnreachable)
                throw new WorkerException($"{slot.Name} could not read the source from Cove: {result.Error}", transient: true, result.ErrorCode);
            Interlocked.Exchange(ref slot.ConsecutiveFailures, 0);

            if (result.Status is TaskStates.Failed or TaskStates.Cancelled)
            {
                Finish(item, slot, JobUnitOutcome.Failed, $"{slot.Name}: {result.Error ?? "generation failed"}");
                return;
            }

            var errors = await CollectAsync(item, assignment, result);
            Finish(item, slot,
                errors.Count == 0 ? JobUnitOutcome.Succeeded : JobUnitOutcome.Failed,
                errors.Count == 0 ? null : $"{slot.Name}: {string.Join("; ", errors)}");
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            OnTransientFailure(slot, item, new WorkerException(
                $"{slot.Name}: did not finish within {runningTimeout.TotalMinutes:0} minutes", transient: true));
        }
        catch (WorkerException ex) when (ex.Transient)
        {
            OnTransientFailure(slot, item, ex);
        }
        catch (Exception ex)
        {
            Finish(item, slot, JobUnitOutcome.Failed, $"{slot.Name}: {ex.Message}");
        }
        finally
        {
            access.Remove(taskId);
            TryDeleteDirectory(uploadDir);
        }
    }

    internal static IReadOnlyList<string> RequestedKinds(WorkItem item)
    {
        var kinds = new List<string>();
        if (item.Cover) kinds.Add(ArtifactKinds.Cover);
        if (item.Preview) kinds.Add(ArtifactKinds.Preview);
        if (item.Sprite) kinds.AddRange([ArtifactKinds.Sprite, ArtifactKinds.Vtt]);
        return kinds;
    }

    private TaskRequest BuildRequest(WorkItem item, string taskId, string coveBaseUrl)
    {
        var preview = _run.Preview;
        var taskUrl = $"{coveBaseUrl.TrimEnd('/')}/api/ext/{extensionId}/worker/tasks/{Uri.EscapeDataString(taskId)}";
        return new TaskRequest(
            taskId,
            item.VideoId,
            $"{taskUrl}/source",
            item.SourceSize,
            item.Duration,
            RequestedKinds(item).ToDictionary(kind => kind, kind => $"{taskUrl}/artifacts/{kind}"),
            item.Cover ? new CoverSpec(item.CoverSeek, item.CoverFilter) : null,
            item.Preview
                ? new PreviewSpec(preview.Segments, preview.SegmentDuration, preview.ExcludeStart, preview.ExcludeEnd,
                    preview.Preset, preview.Audio, PreviewSettings.Crf, PreviewSettings.Width, item.PreviewScale)
                : null,
            item.Sprite ? new SpriteSpec(SpriteSettings.MaxFrames, item.SpriteWidth, item.SpriteFilter, GeneratedPaths.SpriteFileName(item.VideoId)) : null,
            item.Phash ? new PhashSpec() : null);
    }

    /// <summary>Commit every requested artifact the worker produced and uploaded. Returns what is missing.</summary>
    private async Task<List<string>> CollectAsync(WorkItem item, TaskAssignment assignment, TaskResult result)
    {
        var errors = new List<string>();
        var paths = _run.Paths;
        var id = item.VideoId;

        if (item.Cover)
        {
            if (Uploaded(assignment, result, ArtifactKinds.Cover, out var file, out var error))
                ArtifactCommitter.CommitSingle(file, paths.Cover(id), _run.Overwrite);
            else
                errors.Add(error);
        }

        if (item.Preview)
        {
            if (Uploaded(assignment, result, ArtifactKinds.Preview, out var file, out var error))
                ArtifactCommitter.CommitSingle(file, paths.Preview(id), _run.Overwrite);
            else
                errors.Add(error);
        }

        if (item.Sprite)
        {
            if (!Uploaded(assignment, result, ArtifactKinds.Sprite, out var sprite, out var error)
                || !Uploaded(assignment, result, ArtifactKinds.Vtt, out var vtt, out error))
                errors.Add(error);
            else
                ArtifactCommitter.CommitSpritePair(sprite, vtt, paths.Sprite(id), paths.SpriteVtt(id), _run.Overwrite);
        }

        if (item.Phash)
        {
            if (result.Artifacts.TryGetValue(ArtifactKinds.Phash, out var phash) && phash is { Succeeded: true, Value: { Length: > 0 } value })
                await fingerprints.SavePhashAsync(item.FileId, value, _token);
            else
                errors.Add($"phash failed{(phash?.Error is { Length: > 0 } reason ? $" ({reason})" : "")}");
        }

        return errors;
    }

    /// <summary>The worker says it produced the artifact; check that exactly that file arrived.</summary>
    private static bool Uploaded(TaskAssignment assignment, TaskResult result, string kind, out string file, out string error)
    {
        file = "";
        if (!result.Artifacts.TryGetValue(kind, out var artifact) || !artifact.Succeeded)
        {
            error = $"{kind} failed{(artifact?.Error is { Length: > 0 } reason ? $" ({reason})" : "")}";
            return false;
        }
        if (assignment.Upload(kind) is not { } upload)
        {
            error = $"{kind} was not uploaded";
            return false;
        }
        if (artifact.Size is { } size && size != upload.Size)
        {
            error = $"{kind} upload was truncated ({upload.Size} of {size} bytes)";
            return false;
        }
        if (artifact.Sha256 is { Length: > 0 } sha && !string.Equals(sha, upload.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{kind} upload failed its checksum";
            return false;
        }

        file = upload.Path;
        error = "";
        return true;
    }

    // ---- outcomes ---------------------------------------------------------------------------

    private void OnTransientFailure(Slot slot, WorkItem item, WorkerException ex)
    {
        var failures = Interlocked.Increment(ref slot.ConsecutiveFailures);
        logger.LogWarning("Remote generation of video {VideoId} on {Worker} failed (attempt {Attempt}): {Error}",
            item.VideoId, slot.Name, item.Attempts, ex.Message);
        // A dropped connection takes the worker out at once; the reviver brings it back when it reconnects.
        if (workers.GetSession(slot.Live.WorkerId) is null)
            MarkDead(slot, ex.Message);
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
        logger.LogWarning("Remote worker {Worker} taken out of the run: {Reason}", slot.Name, reason);
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
            + $"{Volatile.Read(ref _counters.Skipped)} skipped · {live} worker{(live == 1 ? "" : "s")} live");

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _queue.Writer.TryComplete();
            _allDone.TrySetResult();
        }
    }

    // ---- worker recovery --------------------------------------------------------------------

    private async Task ReviveLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(timings.ReviveInterval, time, _token);

                foreach (var slot in _slots.Where(s => s.Dead))
                {
                    if (workers.GetSession(slot.Live.WorkerId) is null)
                        continue;
                    lock (slot)
                    {
                        slot.Dead = false;
                        slot.ConsecutiveFailures = 0;
                    }
                    logger.LogInformation("Remote worker {Worker} is back; resuming work on it", slot.Name);
                    StartWorkers(slot);
                }

                var now = time.GetUtcNow();
                if (_slots.All(s => s.Dead && Volatile.Read(ref s.ActiveWorkers) == 0 && now - s.DeadSince >= timings.AllDeadGrace))
                {
                    var reasons = string.Join("; ", _slots.Select(s => $"{s.Name}: {s.LastError}"));
                    while (_queue.Reader.TryRead(out var item))
                        Finish(item, null, JobUnitOutcome.Failed, $"No live remote workers ({reasons})");
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
