using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;

namespace RemoteHeavylifter.Worker.Tasks;

/// <summary>How a running task reports back to the Cove session that submitted it.</summary>
public interface ITaskReporter
{
    Task ProgressAsync(TaskProgress progress);
    Task FinishedAsync(TaskResult result);
}

/// <summary>
/// Runs submitted tasks, at most <see cref="WorkerOptions.MaxConcurrency"/> at once across all Cove sessions. A task
/// reads its source from Cove over HTTP, generates cover → preview → sprite in turn (a failed step does not stop the
/// next), uploads each artifact as it is done, and reports the result. Nothing outlives the task: scratch is deleted
/// and the task is cancelled if its session goes away.
/// </summary>
public sealed class TaskRunner(WorkerOptions options, WorkerToken token, CoveHttpClient cove, ILogger<TaskRunner> logger)
{
    private static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>
    {
        [ArtifactKinds.Cover] = 1.0,
        [ArtifactKinds.Preview] = 4.0,
        [ArtifactKinds.Sprite] = 3.0,
    };

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly object _gate = new();

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
            var duration = await MediaProbe.DurationAsync(options.Media, MediaSource.ForCove(request.SourceUrl, token.Value, 0), ct);
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
        var artifacts = new Dictionary<string, ArtifactResult>();
        var steps = new List<string>();
        if (request.Cover is not null) steps.Add(ArtifactKinds.Cover);
        if (request.Preview is not null) steps.Add(ArtifactKinds.Preview);
        if (request.Sprite is not null) steps.Add(ArtifactKinds.Sprite);
        var total = steps.Sum(s => Weights[s]);
        var done = 0.0;

        logger.LogInformation("Task {TaskId}: video {VideoId}, {Steps}", request.TaskId, request.VideoId, string.Join("+", steps));
        try
        {
            Directory.CreateDirectory(workDir);
            await cove.CheckSourceAsync(request.SourceUrl, ct);
            var source = MediaSource.ForCove(request.SourceUrl, token.Value, request.SourceSize);
            var duration = request.Duration > 0 ? request.Duration : await MediaProbe.DurationAsync(options.Media, source, ct);

            foreach (var step in steps)
            {
                await reporter.ProgressAsync(new TaskProgress(request.TaskId, Math.Round(done / total, 4), step));
                try
                {
                    var stepDir = Path.Combine(workDir, step);
                    switch (step)
                    {
                        case ArtifactKinds.Cover:
                        {
                            var output = Path.Combine(workDir, "cover.jpg");
                            await CoverGenerator.GenerateAsync(options.Media, source, duration, request.Cover!, stepDir, output, ct);
                            artifacts[step] = await UploadAsync(request, step, output, ct);
                            break;
                        }
                        case ArtifactKinds.Preview:
                        {
                            var output = Path.Combine(workDir, "preview.mp4");
                            await PreviewGenerator.GenerateAsync(options.Media, source, duration, request.Preview!, stepDir, output, ct, logger);
                            artifacts[step] = await UploadAsync(request, step, output, ct);
                            break;
                        }
                        case ArtifactKinds.Sprite:
                        {
                            var sprite = Path.Combine(workDir, "sprite.jpg");
                            var vtt = Path.Combine(workDir, "thumbs.vtt");
                            await SpriteGenerator.GenerateAsync(options.Media, source, duration, request.Sprite!, stepDir, sprite, vtt, ct, logger);
                            artifacts[ArtifactKinds.Sprite] = await UploadAsync(request, ArtifactKinds.Sprite, sprite, ct);
                            artifacts[ArtifactKinds.Vtt] = await UploadAsync(request, ArtifactKinds.Vtt, vtt, ct);
                            break;
                        }
                    }
                }
                catch (MediaException ex)
                {
                    logger.LogWarning("Task {TaskId}: {Error}", request.TaskId, ex.Message);
                    foreach (var kind in step == ArtifactKinds.Sprite ? [ArtifactKinds.Sprite, ArtifactKinds.Vtt] : new[] { step })
                        artifacts.TryAdd(kind, new ArtifactResult(ArtifactStates.Failed, null, null, ex.Message));
                }
                done += Weights[step];
            }

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
            TryDelete(workDir);
        }
    }

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

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
