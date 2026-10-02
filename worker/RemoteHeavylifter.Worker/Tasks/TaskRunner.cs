using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Transport;

namespace RemoteHeavylifter.Worker.Tasks;

/// <summary>How a running task reports back to the Cove session that submitted it.</summary>
public interface ITaskReporter
{
    Task ProgressAsync(TaskProgress progress);
    Task FinishedAsync(TaskResult result);
}

/// <summary>
/// Runs submitted tasks, at most <see cref="WorkerOptions.MaxConcurrency"/> at once across all Cove sessions. A task
/// reads its source from Cove over HTTP (through the worker's <see cref="SourceCache"/>), generates cover → preview →
/// sprite → phash in turn (a failed step does not stop the next), uploads each artifact as it is done, and reports the
/// result. Nothing outlives the task: each step's scratch is deleted as soon as the step ends, and the task is
/// cancelled if its session goes away.
/// </summary>
public sealed class TaskRunner(
    WorkerOptions options, WorkerToken token, CoveHttpClient cove, SourceCache sourceCache, IMediaEngine engine,
    ILogger<TaskRunner> logger)
{
    private static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>
    {
        [ArtifactKinds.Cover] = 1.0,
        [ArtifactKinds.Preview] = 4.0,
        [ArtifactKinds.Sprite] = 3.0,
        [ArtifactKinds.Phash] = 2.0,
    };

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _nextDevice = -1;

    public int Capacity => options.MaxConcurrency;
    public int Running => _running.Count;

    /// <summary>Start a task unless the worker is full. Returns false when busy.</summary>
    public bool TryStart(TaskRequest request, ITaskReporter reporter, CancellationToken sessionToken)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running.Count >= options.MaxConcurrency || _running.ContainsKey(request.TaskId))
                return false;
            cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            _running[request.TaskId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await RunAsync(request, reporter, cts.Token);
                await reporter.FinishedAsync(result);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not report task {TaskId}", request.TaskId);
            }
            finally
            {
                _running.TryRemove(request.TaskId, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);
        return true;
    }

    public void Cancel(string taskId)
    {
        if (_running.TryGetValue(taskId, out var cts))
            cts.Cancel();
    }

    /// <summary>Can this worker read the URL through Cove? Used by Cove's Test button.</summary>
    public async Task<ProbeResult> ProbeAsync(ProbeParams request, CancellationToken ct)
    {
        try
        {
            await cove.CheckSourceAsync(request.SourceUrl, ct);
            var duration = await engine.ProbeDurationAsync(MediaSource.ForCove(request.SourceUrl, token.Value, 0), ct);
            return duration > 0
                ? new ProbeResult(true, duration, null)
                : new ProbeResult(false, null, "ffprobe could not read the video over HTTP");
        }
        catch (CoveUnreachableException ex)
        {
            return new ProbeResult(false, null, ex.Message);
        }
    }

    internal async Task<TaskResult> RunAsync(TaskRequest request, ITaskReporter reporter, CancellationToken ct)
    {
        var workDir = Path.Combine(options.TasksDir, SafeName(request.TaskId));
        var artifacts = new ConcurrentDictionary<string, ArtifactResult>(StringComparer.Ordinal);
        var steps = new List<string>();
        if (request.Cover is not null) steps.Add(ArtifactKinds.Cover);
        if (request.Preview is not null) steps.Add(ArtifactKinds.Preview);
        if (request.Sprite is not null) steps.Add(ArtifactKinds.Sprite);
        if (request.Phash is not null) steps.Add(ArtifactKinds.Phash);

        logger.LogInformation("Task {TaskId}: video {VideoId}, {Steps}", request.TaskId, request.VideoId, string.Join("+", steps));
        try
        {
            Directory.CreateDirectory(workDir);
            var length = await cove.CheckSourceAsync(request.SourceUrl, ct) ?? request.SourceSize;
            using var cached = await sourceCache.OpenAsync(request.SourceUrl, length, ct);
            var source = cached is not null
                ? MediaSource.ForLocal(cached.LocalUrl, length)
                : MediaSource.ForCove(request.SourceUrl, token.Value, request.SourceSize);
            var duration = request.Duration > 0 ? request.Duration : await engine.ProbeDurationAsync(source, ct);
            if (options.HwAccel is { } hwAccel)
            {
                var device = NextDevice();
                source = source.WithHardwareDecode(hwAccel, device);
                logger.LogInformation("Task {TaskId}: decoding with {HwAccel}{Device}", request.TaskId, hwAccel, device is null ? "" : $" on device {device}");
            }

            var total = steps.Sum(s => Weights[s]);
            var done = 0.0;
            foreach (var step in steps)
            {
                await reporter.ProgressAsync(new TaskProgress(request.TaskId, Math.Round(done / total, 4), step));
                var before = cached?.Stats;
                await RunStepAsync(request, step, source, duration, workDir, artifacts, ct);
                if (cached is not null)
                    LogCache(request.TaskId, step, cached.Stats - before!);
                done += Weights[step];
            }
            if (cached is not null)
                LogCache(request.TaskId, "task", cached.Stats);

            return Finalize(request.TaskId, artifacts);
        }
        catch (CoveUnreachableException ex)
        {
            logger.LogWarning("Task {TaskId}: {Error}", request.TaskId, ex.Message);
            return new TaskResult(request.TaskId, TaskStates.Failed, ex.Message, ErrorCodes.SourceUnreachable, artifacts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new TaskResult(request.TaskId, TaskStates.Cancelled, "cancelled", null, artifacts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task {TaskId} failed unexpectedly", request.TaskId);
            return new TaskResult(request.TaskId, TaskStates.Failed, $"internal error: {ex.Message}", ErrorCodes.Internal, artifacts);
        }
        finally
        {
            Outputs.RemoveDirQuietly(workDir);
        }
    }

    /// <summary>Generates and uploads one artifact; a failure with hardware decoding is retried in software. Its scratch
    /// and outputs live in their own directory, deleted as soon as the step is over.</summary>
    private async Task RunStepAsync(
        TaskRequest request, string step, MediaSource source, double duration, string workDir,
        ConcurrentDictionary<string, ArtifactResult> artifacts, CancellationToken ct)
    {
        var stepDir = Path.Combine(workDir, step);
        var stopwatch = Stopwatch.StartNew();
        var outcome = "done";
        try
        {
            try
            {
                await GenerateStepAsync(request, step, source, duration, stepDir, artifacts, ct);
            }
            catch (MediaException ex) when (source.HardwareDecode)
            {
                logger.LogWarning("Task {TaskId}: {Step} failed with hardware decoding ({Error}); retrying in software",
                    request.TaskId, step, ex.Message);
                Outputs.RemoveDirQuietly(stepDir);
                outcome = "done in software";
                await GenerateStepAsync(request, step, source.Software, duration, stepDir, artifacts, ct);
            }
        }
        catch (MediaException ex)
        {
            outcome = "failed";
            logger.LogWarning("Task {TaskId}: {Error}", request.TaskId, ex.Message);
            foreach (var kind in step == ArtifactKinds.Sprite ? [ArtifactKinds.Sprite, ArtifactKinds.Vtt] : new[] { step })
                artifacts.TryAdd(kind, new ArtifactResult(ArtifactStates.Failed, null, null, ex.Message));
        }
        catch
        {
            outcome = "stopped";
            throw;
        }
        finally
        {
            Outputs.RemoveDirQuietly(stepDir);
            logger.LogInformation("Task {TaskId}: {Step} {Outcome} in {Seconds:0.0} s",
                request.TaskId, step, outcome, stopwatch.Elapsed.TotalSeconds);
        }
    }

    private async Task GenerateStepAsync(
        TaskRequest request, string step, MediaSource source, double duration, string stepDir,
        ConcurrentDictionary<string, ArtifactResult> artifacts, CancellationToken ct)
    {
        switch (step)
        {
            case ArtifactKinds.Cover:
            {
                var output = Path.Combine(stepDir, "cover.jpg");
                await CoverGenerator.GenerateAsync(engine, source, duration, request.Cover!, stepDir, output, ct);
                artifacts[step] = await UploadAsync(request, step, output, ct);
                break;
            }
            case ArtifactKinds.Preview:
            {
                var output = Path.Combine(stepDir, "preview.mp4");
                await PreviewGenerator.GenerateAsync(engine, source, duration, request.Preview!, stepDir, output, ct);
                artifacts[step] = await UploadAsync(request, step, output, ct);
                break;
            }
            case ArtifactKinds.Sprite:
            {
                var sprite = Path.Combine(stepDir, "sprite.jpg");
                var vtt = Path.Combine(stepDir, "thumbs.vtt");
                await SpriteGenerator.GenerateAsync(engine, source, duration, request.Sprite!, stepDir, sprite, vtt, ct, options.SpriteKeyframes);
                artifacts[ArtifactKinds.Sprite] = await UploadAsync(request, ArtifactKinds.Sprite, sprite, ct);
                artifacts[ArtifactKinds.Vtt] = await UploadAsync(request, ArtifactKinds.Vtt, vtt, ct);
                break;
            }
            case ArtifactKinds.Phash:
            {
                var phash = await PhashGenerator.GenerateAsync(engine, source, duration, request.Phash!, stepDir, ct);
                artifacts[step] = new ArtifactResult(ArtifactStates.Succeeded, null, null, null, phash);
                break;
            }
        }
    }

    /// <summary>The hardware decoding device for the next task, taking <see cref="WorkerOptions.HwAccelDevices"/> in turn.</summary>
    private string? NextDevice()
    {
        var devices = options.HwAccelDevices;
        return devices.Count == 0 ? null : devices[(int)((uint)Interlocked.Increment(ref _nextDevice) % (uint)devices.Count)];
    }

    private void LogCache(string taskId, string scope, CacheStats stats) =>
        logger.LogInformation(
            "Task {TaskId}: cache ({Scope}): {HitRate:P0} hit ({Hits} hits, {Waits} shared, {Misses} misses); "
            + "{Fetched:0.0} MB from Cove in {Fetches} requests, {FetchSeconds:0.00} s; {Served:0.0} MB to ffmpeg in {Reads} reads",
            taskId, scope, stats.HitRate, stats.Hits, stats.Waits, stats.Misses,
            stats.BytesFetched / 1048576.0, stats.Fetches, stats.FetchTime.TotalSeconds, stats.BytesServed / 1048576.0, stats.Reads);

    private async Task<ArtifactResult> UploadAsync(TaskRequest request, string kind, string path, CancellationToken ct)
    {
        if (!request.UploadUrls.TryGetValue(kind, out var url))
            return new ArtifactResult(ArtifactStates.Failed, null, null, "Cove gave no upload URL");
        await cove.UploadAsync(url, path, ct);
        return new ArtifactResult(ArtifactStates.Succeeded, new FileInfo(path).Length, await Outputs.Sha256FileAsync(path, ct), null);
    }

    /// <summary>All requested artifacts succeeded → succeeded; some → partial; none → failed with their errors.</summary>
    internal static TaskResult Finalize(string taskId, IReadOnlyDictionary<string, ArtifactResult> artifacts)
    {
        var succeeded = artifacts.Values.Count(a => a.Succeeded);
        if (succeeded == artifacts.Count && succeeded > 0)
            return new TaskResult(taskId, TaskStates.Succeeded, null, null, artifacts);
        if (succeeded > 0)
            return new TaskResult(taskId, TaskStates.Partial, null, null, artifacts);

        var errors = artifacts.Values.Select(a => a.Error).Where(e => !string.IsNullOrEmpty(e)).Distinct().Order(StringComparer.Ordinal).ToList();
        return new TaskResult(taskId, TaskStates.Failed,
            errors.Count > 0 ? string.Join("; ", errors) : "no artifacts were produced", ErrorCodes.GenerationFailed, artifacts);
    }

    private static string SafeName(string taskId)
        => string.Concat(taskId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
