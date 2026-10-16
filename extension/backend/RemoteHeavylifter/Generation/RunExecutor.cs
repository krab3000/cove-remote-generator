using System.Globalization;
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

/// <summary>A worker taking part in a run: its slot pools, and how it reaches Cove.</summary>
/// <param name="DirectMappings">The worker's path mappings when it reads sources from its own filesystem; null when it
/// reads them over HTTP.</param>
public sealed record LiveWorker(
    Guid WorkerId, string Name, IReadOnlyList<SlotInfo> Slots, string CoveBaseUrl, IReadOnlyList<PathMapping>? DirectMappings = null);

/// <param name="CpuHelpsGpu">Idle CPU slots take videos a GPU could decode while every GPU slot is busy.</param>
public sealed record RunContext(string RunId, GeneratedPaths Paths, PreviewSettings Preview, bool Overwrite, bool CpuHelpsGpu = false);

/// <summary>Unit totals across a run, including videos settled before any remote work.</summary>
public sealed class RunCounters
{
    public int Total;
    public int Succeeded;
    public int Failed;
    public int Skipped;

    public int Done => Volatile.Read(ref Succeeded) + Volatile.Read(ref Failed) + Volatile.Read(ref Skipped);
}

/// <summary>What one task of a video does.</summary>
internal enum PartKind
{
    /// <summary>Everything the video needs, on a GPU that decodes it.</summary>
    All,

    /// <summary>Only the preview: GPU work even when no GPU decodes the video, for the hardware encoder.</summary>
    Preview,

    /// <summary>Cover, sprite and phash: decoding work, on a CPU slot when no GPU decodes the video.</summary>
    Rest,
}

/// <summary>
/// Spreads one run's videos over the slots of all live workers as one pool. A video a GPU decodes is one task for a GPU
/// slot that decodes it; one no GPU decodes is a preview task for a GPU slot (decoded in software, encoded on the GPU)
/// and a task for the rest on a CPU slot. Each slot runs as many loops as its capacity; a free loop takes the oldest
/// task it may run, so no work is assigned ahead and every slot stays busy while there is work it can do. The worker
/// reads the source and uploads the artifacts through the extension's HTTP endpoints; the loop commits what arrived.
/// A worker that keeps failing or disconnects is marked dead: its loops stop, its tasks go back to the others, and a
/// reviver brings it back when its session reconnects.
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
    private sealed class WorkerState(LiveWorker live)
    {
        public LiveWorker Live { get; } = live;
        public string Name => Live.Name;
        public volatile bool Dead;
        public DateTimeOffset DeadSince;
        public string? LastError;
        public int ConsecutiveFailures;
        public List<SlotState> Slots { get; } = [];
        public int ActiveLoops => Slots.Sum(s => Volatile.Read(ref s.ActiveLoops));
    }

    private sealed class SlotState(WorkerState worker, SlotInfo info)
    {
        public WorkerState Worker { get; } = worker;
        public SlotInfo Info { get; } = info;
        public bool Gpu => Info.Kind == SlotKinds.Gpu;
        public string Name => $"{Info.Id}@{Worker.Name}";
        public int ActiveLoops;
        public int Busy;
        public int Succeeded;
    }

    private sealed class Part(WorkItem item, PartKind kind)
    {
        public WorkItem Item { get; } = item;
        public PartKind Kind { get; set; } = kind;
        public int Attempts;

        public bool Cover => Kind != PartKind.Preview && Item.Cover;
        public bool Preview => Kind != PartKind.Rest && Item.Preview;
        public bool Sprite => Kind != PartKind.Preview && Item.Sprite;
        public bool Phash => Kind != PartKind.Preview && Item.Phash;
        public string Tag => Kind.ToString().ToLowerInvariant();
    }

    private readonly object _gate = new();
    private readonly LinkedList<Part> _pending = [];
    private TaskCompletionSource _changed = NewSignal();
    private readonly TaskCompletionSource _allDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _loops = [];
    private List<WorkerState> _workers = [];
    private RunContext _run = null!;
    private IJobProgress _progress = null!;
    private RunCounters _counters = null!;
    private CancellationToken _token;
    private int _remaining;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <returns>Parts finished per slot (<c>slot@worker</c>).</returns>
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

        _workers = live.Select(w => new WorkerState(w)).ToList();
        foreach (var worker in _workers)
            worker.Slots.AddRange(worker.Live.Slots.Where(s => s.Capacity > 0).Select(s => new SlotState(worker, s)));
        var slots = _workers.SelectMany(w => w.Slots).ToList();
        foreach (var item in items)
            foreach (var part in Split(item, slots))
                _pending.AddLast(part);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _token = runCts.Token;
        foreach (var worker in _workers)
            StartLoops(worker);
        var reviver = Task.Run(ReviveLoopAsync, CancellationToken.None);

        try
        {
            await _allDone.Task.WaitAsync(ct);
        }
        finally
        {
            await runCts.CancelAsync();
            Signal();
            Task[] all;
            lock (_loops)
                all = [.. _loops, reviver];
            await Task.WhenAll(all).ContinueWith(static _ => { }, TaskScheduler.Default);
        }

        return _workers.SelectMany(w => w.Slots).ToDictionary(s => s.Name, s => s.Succeeded);
    }

    // ---- routing ----------------------------------------------------------------------------

    /// <summary>A video's tasks: one for a GPU that decodes it, else a preview for a GPU and the rest for a CPU (one task
    /// when the pool lacks GPU or CPU slots altogether).</summary>
    private static List<Part> Split(WorkItem item, IReadOnlyList<SlotState> slots)
    {
        var anyGpu = slots.Any(s => s.Gpu);
        var anyCpu = slots.Any(s => !s.Gpu);
        var decodable = slots.Any(s => s.Gpu && s.Info.Decodes(item.Codec, item.Width, item.Height));
        List<Part> parts;
        if (decodable || !anyGpu || !anyCpu)
            parts = [new Part(item, PartKind.All)];
        else
        {
            parts = [];
            if (item.Preview)
                parts.Add(new Part(item, PartKind.Preview));
            if (item.Cover || item.Sprite || item.Phash)
                parts.Add(new Part(item, PartKind.Rest));
        }
        item.PendingParts = parts.Count;
        return parts;
    }

    /// <summary>The oldest pending part <paramref name="slot"/> may run, taken off the queue; null when none. Under _gate.</summary>
    private Part? TakeFor(SlotState slot)
    {
        var live = _workers.Where(w => !w.Dead).SelectMany(w => w.Slots).ToList();
        var liveGpu = live.Where(s => s.Gpu).ToList();
        var liveCpu = live.Any(s => !s.Gpu);

        bool Eligible(Part part)
        {
            var item = part.Item;
            if (slot.Gpu)
                return part.Kind switch
                {
                    // The GPU that decodes it first; when none of those is live, any GPU (decoding in software).
                    PartKind.All => slot.Info.Decodes(item.Codec, item.Width, item.Height)
                                    || !liveGpu.Any(s => s.Info.Decodes(item.Codec, item.Width, item.Height)),
                    PartKind.Preview => true,
                    _ => !liveCpu,
                };
            return part.Kind == PartKind.Rest || liveGpu.Count == 0;
        }

        for (var node = _pending.First; node is not null; node = node.Next)
        {
            if (!Eligible(node.Value))
                continue;
            _pending.Remove(node);
            return node.Value;
        }

        // A CPU slot with nothing of its own helps with GPU work when allowed and the GPUs are backed up: it takes the
        // cover, sprite and phash of a waiting video and leaves its preview to a GPU.
        if (!slot.Gpu && _run.CpuHelpsGpu)
        {
            var freeGpu = liveGpu.Sum(s => Math.Max(0, s.Info.Capacity - Volatile.Read(ref s.Busy)));
            var waiting = _pending.Count(p => p.Kind == PartKind.All);
            for (var node = _pending.First; node is not null && waiting > freeGpu; node = node.Next)
            {
                var part = node.Value;
                if (part.Kind != PartKind.All || !(part.Item.Cover || part.Item.Sprite || part.Item.Phash))
                    continue;
                _pending.Remove(node);
                if (part.Item.Preview)
                {
                    Interlocked.Increment(ref part.Item.PendingParts);
                    _pending.AddFirst(new Part(part.Item, PartKind.Preview));
                }
                part.Kind = PartKind.Rest;
                return part;
            }
        }
        return null;
    }

    /// <summary>The next part for <paramref name="slot"/>, waiting for one; null when the run is over or the worker died.</summary>
    private async Task<Part?> NextAsync(SlotState slot)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_allDone.Task.IsCompleted || slot.Worker.Dead || _token.IsCancellationRequested)
                    return null;
                if (TakeFor(slot) is { } part)
                {
                    Interlocked.Increment(ref slot.Busy);
                    return part;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(_token);
        }
    }

    /// <summary>Wakes every waiting loop to look at the queue again.</summary>
    private void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
    }

    private void Enqueue(Part part)
    {
        lock (_gate)
            _pending.AddLast(part);
        Signal();
    }

    // ---- loops ------------------------------------------------------------------------------

    private void StartLoops(WorkerState worker)
    {
        foreach (var slot in worker.Slots)
        {
            var missing = slot.Info.Capacity - Volatile.Read(ref slot.ActiveLoops);
            for (var i = 0; i < missing; i++)
            {
                Interlocked.Increment(ref slot.ActiveLoops);
                lock (_loops)
                    _loops.Add(Task.Run(() => LoopAsync(slot), CancellationToken.None));
            }
        }
    }

    private async Task LoopAsync(SlotState slot)
    {
        try
        {
            while (await NextAsync(slot) is { } part)
            {
                try
                {
                    await ProcessAsync(slot, part);
                }
                finally
                {
                    Interlocked.Decrement(ref slot.Busy);
                    Signal(); // a free GPU slot changes what a helping CPU slot may take
                }
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Remote generation loop for {Slot} stopped unexpectedly", slot.Name);
        }
        finally
        {
            Interlocked.Decrement(ref slot.ActiveLoops);
        }
    }

    private async Task ProcessAsync(SlotState slot, Part part)
    {
        var worker = slot.Worker;
        var item = part.Item;
        if (workers.GetSession(worker.Live.WorkerId) is not { } session)
        {
            // The connection dropped between runs of this loop; hand the part to someone else.
            MarkDead(worker, "disconnected");
            Enqueue(part);
            return;
        }

        item.Unit ??= _progress.StartUnit(item.VideoId.ToString(CultureInfo.InvariantCulture), item.Label);
        var attempt = Interlocked.Increment(ref part.Attempts);
        var taskId = $"{_run.RunId}-{item.VideoId}-{part.Tag}-a{attempt}";
        var uploadDir = Path.Combine(_run.Paths.TempRoot, _run.RunId, item.VideoId.ToString(CultureInfo.InvariantCulture), $"{part.Tag}-a{attempt}");
        var assignment = new TaskAssignment(taskId, worker.Live.WorkerId, item.SourcePath, RequestedKinds(part), uploadDir);
        access.Register(assignment);
        item.Unit.Report(0, $"{slot.Name}: submitting");

        var runningTimeout = timings.RunningTimeout + TimeSpan.FromSeconds(Math.Max(0, item.Duration) / 4);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
        timeout.CancelAfter(runningTimeout);
        try
        {
            var result = await session.RunTaskAsync(
                BuildRequest(part, taskId, worker.Live, slot.Info.Id),
                p => item.Unit?.Report(p.Progress, $"{slot.Name}: {p.Stage ?? "working"}"),
                timeout.Token);

            // A worker that cannot reach Cove is a problem with that worker, not the video: count it against
            // the worker (so it is taken out after a few) and let another one try.
            if (result.ErrorCode == ErrorCodes.SourceUnreachable)
                throw new WorkerException($"{worker.Name} could not read the source from Cove: {result.Error}", transient: true, result.ErrorCode);
            Interlocked.Exchange(ref worker.ConsecutiveFailures, 0);

            if (result.Status is TaskStates.Failed or TaskStates.Cancelled)
            {
                PartDone(part, slot, JobUnitOutcome.Failed, $"{slot.Name}: {result.Error ?? "generation failed"}");
                return;
            }

            var errors = await CollectAsync(part, assignment, result);
            PartDone(part, slot,
                errors.Count == 0 ? JobUnitOutcome.Succeeded : JobUnitOutcome.Failed,
                errors.Count == 0 ? null : $"{slot.Name}: {string.Join("; ", errors)}");
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            OnTransientFailure(slot, part, new WorkerException(
                $"{slot.Name}: did not finish within {runningTimeout.TotalMinutes:0} minutes", transient: true));
        }
        catch (WorkerException ex) when (ex.Transient)
        {
            OnTransientFailure(slot, part, ex);
        }
        catch (Exception ex)
        {
            PartDone(part, slot, JobUnitOutcome.Failed, $"{slot.Name}: {ex.Message}");
        }
        finally
        {
            access.Remove(taskId);
            TryDeleteDirectory(uploadDir);
        }
    }

    private static IReadOnlyList<string> RequestedKinds(Part part)
    {
        var kinds = new List<string>();
        if (part.Cover) kinds.Add(ArtifactKinds.Cover);
        if (part.Preview) kinds.Add(ArtifactKinds.Preview);
        if (part.Sprite) kinds.AddRange([ArtifactKinds.Sprite, ArtifactKinds.Vtt]);
        return kinds;
    }

    /// <summary>Every uploaded kind a whole video asks for.</summary>
    internal static IReadOnlyList<string> RequestedKinds(WorkItem item) => RequestedKinds(new Part(item, PartKind.All));

    private TaskRequest BuildRequest(Part part, string taskId, LiveWorker live, string slotId)
    {
        var item = part.Item;
        var preview = _run.Preview;
        var taskUrl = $"{live.CoveBaseUrl.TrimEnd('/')}/api/ext/{extensionId}/worker/tasks/{Uri.EscapeDataString(taskId)}";
        return new TaskRequest(
            taskId,
            item.VideoId,
            $"{taskUrl}/source",
            item.SourceSize,
            item.Duration,
            RequestedKinds(part).ToDictionary(kind => kind, kind => $"{taskUrl}/artifacts/{kind}"),
            part.Cover ? new CoverSpec(item.CoverSeek, item.CoverFilter) : null,
            part.Preview
                ? new PreviewSpec(preview.Segments, preview.SegmentDuration, preview.ExcludeStart, preview.ExcludeEnd,
                    preview.Preset, preview.Audio, PreviewSettings.Crf, PreviewSettings.Width, item.PreviewScale)
                : null,
            part.Sprite ? new SpriteSpec(SpriteSettings.MaxFrames, item.SpriteWidth, item.SpriteFilter, GeneratedPaths.SpriteFileName(item.VideoId)) : null,
            part.Phash ? new PhashSpec() : null,
            live.DirectMappings is { } mappings ? PathMapper.Map(item.CovePath, mappings) : null,
            slotId);
    }

    /// <summary>Commit every artifact the part asked for that the worker produced and uploaded. Returns what is missing.</summary>
    private async Task<List<string>> CollectAsync(Part part, TaskAssignment assignment, TaskResult result)
    {
        var errors = new List<string>();
        var paths = _run.Paths;
        var id = part.Item.VideoId;

        if (part.Cover)
        {
            if (Uploaded(assignment, result, ArtifactKinds.Cover, out var file, out var error))
                ArtifactCommitter.CommitSingle(file, paths.Cover(id), _run.Overwrite);
            else
                errors.Add(error);
        }

        if (part.Preview)
        {
            if (Uploaded(assignment, result, ArtifactKinds.Preview, out var file, out var error))
                ArtifactCommitter.CommitSingle(file, paths.Preview(id), _run.Overwrite);
            else
                errors.Add(error);
        }

        if (part.Sprite)
        {
            if (!Uploaded(assignment, result, ArtifactKinds.Sprite, out var sprite, out var error)
                || !Uploaded(assignment, result, ArtifactKinds.Vtt, out var vtt, out error))
                errors.Add(error);
            else
                ArtifactCommitter.CommitSpritePair(sprite, vtt, paths.Sprite(id), paths.SpriteVtt(id), _run.Overwrite);
        }

        if (part.Phash)
        {
            if (result.Artifacts.TryGetValue(ArtifactKinds.Phash, out var phash) && phash is { Succeeded: true, Value: { Length: > 0 } value })
                await fingerprints.SavePhashAsync(part.Item.FileId, value, _token);
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

    private void OnTransientFailure(SlotState slot, Part part, WorkerException ex)
    {
        var worker = slot.Worker;
        var failures = Interlocked.Increment(ref worker.ConsecutiveFailures);
        logger.LogWarning("Remote generation of video {VideoId} ({Part}) on {Slot} failed (attempt {Attempt}): {Error}",
            part.Item.VideoId, part.Tag, slot.Name, part.Attempts, ex.Message);
        // A dropped connection takes the worker out at once; the reviver brings it back when it reconnects.
        if (workers.GetSession(worker.Live.WorkerId) is null)
            MarkDead(worker, ex.Message);
        if (failures >= timings.FailuresBeforeDead)
            MarkDead(worker, ex.Message);

        if (part.Attempts >= timings.MaxAttempts)
        {
            PartDone(part, null, JobUnitOutcome.Failed, $"Gave up after {part.Attempts} attempts: {ex.Message}");
            return;
        }

        part.Item.Unit?.Report(0, "Waiting to retry");
        Enqueue(part);
    }

    private void MarkDead(WorkerState worker, string reason)
    {
        lock (worker)
        {
            if (worker.Dead)
                return;
            worker.Dead = true;
            worker.DeadSince = time.GetUtcNow();
            worker.LastError = reason;
        }
        logger.LogWarning("Remote worker {Worker} taken out of the run: {Reason}", worker.Name, reason);
        Signal(); // its loops stop; parts only its GPUs decoded may now go to other GPUs
    }

    /// <summary>One part of a video is over; the video is when all its parts are (failed if any failed).</summary>
    private void PartDone(Part part, SlotState? slot, JobUnitOutcome outcome, string? message)
    {
        var item = part.Item;
        if (outcome == JobUnitOutcome.Succeeded && slot is not null)
            Interlocked.Increment(ref slot.Succeeded);
        if (outcome != JobUnitOutcome.Succeeded && message is not null)
        {
            lock (item.Errors)
                item.Errors.Add(message);
        }
        // A part being split off (TakeFor) is counted while its sibling is still pending, so this never hits 0 early.
        if (Interlocked.Decrement(ref item.PendingParts) > 0)
            return;
        Finish(item, item.Errors.Count == 0 ? JobUnitOutcome.Succeeded : JobUnitOutcome.Failed,
            item.Errors.Count == 0 ? null : string.Join("; ", item.Errors));
    }

    private void Finish(WorkItem item, JobUnitOutcome outcome, string? message)
    {
        var unit = item.Unit ??= _progress.StartUnit(item.VideoId.ToString(CultureInfo.InvariantCulture), item.Label);
        unit.Complete(outcome, message);
        unit.Dispose();

        switch (outcome)
        {
            case JobUnitOutcome.Succeeded:
                Interlocked.Increment(ref _counters.Succeeded);
                break;
            case JobUnitOutcome.Failed:
                Interlocked.Increment(ref _counters.Failed);
                break;
            default:
                Interlocked.Increment(ref _counters.Skipped);
                break;
        }

        var live = _workers.Count(w => !w.Dead);
        _progress.Report(
            _counters.Total == 0 ? 1 : (double)_counters.Done / _counters.Total,
            $"{_counters.Done} / {_counters.Total} videos · {Volatile.Read(ref _counters.Failed)} failed · "
            + $"{Volatile.Read(ref _counters.Skipped)} skipped · {live} worker{(live == 1 ? "" : "s")} live");

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _allDone.TrySetResult();
            Signal();
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

                foreach (var worker in _workers.Where(w => w.Dead))
                {
                    if (workers.GetSession(worker.Live.WorkerId) is null)
                        continue;
                    lock (worker)
                    {
                        worker.Dead = false;
                        worker.ConsecutiveFailures = 0;
                    }
                    logger.LogInformation("Remote worker {Worker} is back; resuming work on it", worker.Name);
                    StartLoops(worker);
                    Signal();
                }

                var now = time.GetUtcNow();
                if (_workers.All(w => w.Dead && w.ActiveLoops == 0 && now - w.DeadSince >= timings.AllDeadGrace))
                {
                    var reasons = string.Join("; ", _workers.Select(w => $"{w.Name}: {w.LastError}"));
                    List<Part> stranded;
                    lock (_gate)
                    {
                        stranded = [.. _pending];
                        _pending.Clear();
                    }
                    foreach (var part in stranded)
                        PartDone(part, null, JobUnitOutcome.Failed, $"No live remote workers ({reasons})");
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
