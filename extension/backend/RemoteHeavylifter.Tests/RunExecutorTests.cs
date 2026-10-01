using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Workers;

namespace RemoteHeavylifter.Tests;

/// <summary>
/// A worker session that "generates" by writing <c>{kind}-{videoId}</c> into the task's upload folder, the way the
/// upload endpoint would after a real worker's PUT, and reports it finished.
/// </summary>
internal sealed class FakeWorker(string name, WorkerAccess access, int capacity = 8) : IWorkerSession
{
    public Guid WorkerId { get; } = Guid.NewGuid();
    public string Name { get; } = name;
    public WorkerInfo Info => new(ProtocolInfo.Version, "test", Name, "ffmpeg test", "libx264", capacity, InFlight);
    public string? InboundBaseUrl => null;
    public bool IsOpen => !Down;

    public volatile bool Down;
    public int InFlight;
    public int MaxInFlight;
    public int Submitted;
    public readonly ConcurrentBag<TaskRequest> Requests = [];
    public readonly ConcurrentBag<string> Cancelled = [];

    /// <summary>Decides the result; defaults to every requested artifact succeeding.</summary>
    public Func<TaskRequest, (string Status, string? ErrorCode, string[] FailedKinds)> Behavior { get; set; }
        = _ => (TaskStates.Succeeded, null, []);

    /// <summary>Called on submit; may flip <see cref="Down"/> to simulate a disconnect.</summary>
    public Action<TaskRequest>? OnSubmit { get; set; }

    public bool Hang { get; set; }
    public bool CorruptUploads { get; set; }

    public static string Content(string kind, int videoId) => $"{kind}-{videoId}";

    public static string Phash(int videoId) => $"abc{videoId:x}";

    public async Task<TaskResult> RunTaskAsync(TaskRequest request, Action<TaskProgress>? progress, CancellationToken ct)
    {
        if (Down)
            throw new WorkerException($"{Name}: connection lost", transient: true);
        OnSubmit?.Invoke(request);
        Interlocked.Increment(ref Submitted);
        Requests.Add(request);
        if (Down)
            throw new WorkerException($"{Name}: connection lost", transient: true);

        var now = Interlocked.Increment(ref InFlight);
        int max;
        while (now > (max = Volatile.Read(ref MaxInFlight)) && Interlocked.CompareExchange(ref MaxInFlight, now, max) != max)
        {
        }

        try
        {
            if (Hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.Add(request.TaskId);
                    throw;
                }
            }
            await Task.Yield();
            progress?.Invoke(new TaskProgress(request.TaskId, 0.5, "encoding"));

            var (status, code, failed) = Behavior(request);
            var assignment = access.Get(request.TaskId) ?? throw new InvalidOperationException("task was not assigned");
            var artifacts = new Dictionary<string, ArtifactResult>();
            if (request.Phash is not null)
            {
                artifacts[ArtifactKinds.Phash] = status == TaskStates.Failed || failed.Contains(ArtifactKinds.Phash)
                    ? new ArtifactResult(ArtifactStates.Failed, null, null, "phash broke")
                    : new ArtifactResult(ArtifactStates.Succeeded, null, null, null, Phash(request.VideoId));
            }
            foreach (var kind in request.UploadUrls.Keys)
            {
                if (status == TaskStates.Failed || failed.Contains(kind))
                {
                    artifacts[kind] = new ArtifactResult(ArtifactStates.Failed, null, null, $"{kind} broke");
                    continue;
                }
                var bytes = Encoding.UTF8.GetBytes(Content(kind, request.VideoId));
                Directory.CreateDirectory(assignment.UploadDirectory);
                var path = Path.Combine(assignment.UploadDirectory, kind);
                await File.WriteAllBytesAsync(path, CorruptUploads ? [.. bytes, 0] : bytes, ct);
                var uploaded = await File.ReadAllBytesAsync(path, ct);
                WorkerAccess.RecordUpload(assignment, kind, new UploadedArtifact(path, uploaded.Length, Convert.ToHexStringLower(SHA256.HashData(uploaded))));
                artifacts[kind] = new ArtifactResult(ArtifactStates.Succeeded, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), null);
            }
            return new TaskResult(request.TaskId, status, status == TaskStates.Failed ? "error: it broke" : null, code, artifacts);
        }
        finally
        {
            Interlocked.Decrement(ref InFlight);
        }
    }

    public Task<ProbeResult> ProbeAsync(ProbeParams request, CancellationToken ct) => Task.FromResult(new ProbeResult(true, 1, null));

    public Task<WorkerInfo> RefreshInfoAsync(CancellationToken ct) => Task.FromResult(Info);
}

internal sealed class RecordingFingerprints : IFingerprintStore
{
    public readonly ConcurrentDictionary<int, string> Phashes = new();

    public Task SavePhashAsync(int fileId, string phash, CancellationToken ct)
    {
        Phashes[fileId] = phash;
        return Task.CompletedTask;
    }
}

internal sealed class FakeDirectory(params FakeWorker[] workers) : IWorkerDirectory
{
    public IWorkerSession? GetSession(Guid workerId) => workers.FirstOrDefault(w => w.WorkerId == workerId) is { Down: false } w ? w : null;
}

internal sealed class RecordingProgress : IJobProgress
{
    public readonly ConcurrentDictionary<string, (JobUnitOutcome Outcome, string? Message)> Units = new();
    public string? Summary;

    public void Report(double progress, string? subTask = null)
    {
    }

    public void SetSummary(string summary) => Summary = summary;

    public IJobUnit StartUnit(string unitId, string? label = null) => new Unit(this, unitId);

    private sealed class Unit(RecordingProgress owner, string id) : IJobUnit
    {
        public JobUnitOutcome? Outcome { get; private set; }

        public void Report(double progress, string? message = null)
        {
        }

        public void Complete(JobUnitOutcome outcome, string? message = null)
        {
            Outcome ??= outcome;
            if (!owner.Units.TryAdd(id, (outcome, message)))
                throw new InvalidOperationException($"unit {id} completed twice");
        }

        public void Dispose()
        {
        }
    }
}

public sealed class RunExecutorTests : IDisposable
{
    private static readonly CoordinatorTimings Fast = new(
        ReviveInterval: TimeSpan.FromMilliseconds(20),
        AllDeadGrace: TimeSpan.FromMilliseconds(60),
        RunningTimeout: TimeSpan.FromSeconds(10),
        MaxAttempts: 3,
        FailuresBeforeDead: 2);

    private readonly string _root = Directory.CreateTempSubdirectory("rh-run-").FullName;
    private readonly GeneratedPaths _paths;
    private readonly WorkerAccess _access = new(new WorkerRegistry(new StoreHolder()));
    private readonly RecordingFingerprints _fingerprints = new();

    public RunExecutorTests() => _paths = new GeneratedPaths(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FakeWorker Worker(string name) => new(name, _access);

    private static List<WorkItem> Items(int count) => Enumerable.Range(1, count)
        .Select(id => new WorkItem
        {
            VideoId = id,
            Label = $"video {id}",
            CovePath = $"/cove/v{id}.mp4",
            SourcePath = $"/cove/v{id}.mp4",
            FileId = 100 + id,
            SourceSize = 1000,
            Duration = 60,
            Cover = true,
            Preview = true,
            Sprite = true,
            PreviewScale = "scale=640:-2",
        })
        .ToList();

    private async Task<(RecordingProgress Progress, RunCounters Counters, IReadOnlyDictionary<string, int> PerWorker)> RunAsync(
        IReadOnlyList<WorkItem> items, bool overwrite, CancellationToken ct, params (FakeWorker Worker, int Slots)[] workers)
    {
        var executor = new RunExecutor(new FakeDirectory(workers.Select(w => w.Worker).ToArray()), _access, _fingerprints, "ext.test",
            Fast, TimeProvider.System, NullLogger.Instance);
        var progress = new RecordingProgress();
        var counters = new RunCounters { Total = items.Count };
        var run = new RunContext("run1", _paths, new PreviewSettings(12, 0.75, "0", "0", "slow", false), overwrite);
        var perWorker = await executor.ExecuteAsync(
            workers.Select(w => new LiveWorker(w.Worker.WorkerId, w.Worker.Name, w.Slots, $"http://cove-for-{w.Worker.Name}:5073")).ToList(),
            items, run, progress, counters, ct);
        return (progress, counters, perWorker);
    }

    private void AssertCommitted(int id)
    {
        Assert.Equal("cover-" + id, File.ReadAllText(_paths.Cover(id)));
        Assert.Equal("preview-" + id, File.ReadAllText(_paths.Preview(id)));
        Assert.Equal("sprite-" + id, File.ReadAllText(_paths.Sprite(id)));
        Assert.Equal("vtt-" + id, File.ReadAllText(_paths.SpriteVtt(id)));
    }

    [Fact]
    public async Task Distributes_within_each_workers_slots_and_commits_everything()
    {
        var a = Worker("a");
        var b = Worker("b");
        var (progress, counters, perWorker) = await RunAsync(Items(40), false, TestContext.Current.CancellationToken, (a, 2), (b, 3));

        Assert.Equal(40, counters.Succeeded);
        Assert.All(progress.Units.Values, u => Assert.Equal(JobUnitOutcome.Succeeded, u.Outcome));
        Assert.InRange(a.MaxInFlight, 1, 2);
        Assert.InRange(b.MaxInFlight, 1, 3);
        Assert.Equal(40, perWorker["a"] + perWorker["b"]);
        for (var id = 1; id <= 40; id++)
            AssertCommitted(id);
        Assert.False(Directory.Exists(Path.Combine(_paths.TempRoot, "run1", "1", "a1")));
        Assert.All(a.Requests.Concat(b.Requests), r => Assert.Null(_access.Get(r.TaskId)));
    }

    [Fact]
    public async Task Task_urls_point_at_the_workers_cove_url()
    {
        var a = Worker("a");
        await RunAsync(Items(1), false, TestContext.Current.CancellationToken, (a, 1));

        var request = Assert.Single(a.Requests);
        var taskRoot = $"http://cove-for-a:5073/api/ext/ext.test/worker/tasks/{request.TaskId}";
        Assert.Equal($"{taskRoot}/source", request.SourceUrl);
        Assert.Equal(1000, request.SourceSize);
        Assert.Equal(ArtifactKinds.All.ToHashSet(), request.UploadUrls.Keys.ToHashSet());
        Assert.Equal($"{taskRoot}/artifacts/preview", request.UploadUrls[ArtifactKinds.Preview]);
    }

    [Fact]
    public async Task Work_moves_to_the_other_worker_when_one_disconnects()
    {
        var a = Worker("a");
        var b = Worker("b");
        a.OnSubmit = _ =>
        {
            if (Volatile.Read(ref a.Submitted) >= 3)
                a.Down = true;
        };

        var (_, counters, perWorker) = await RunAsync(Items(20), false, TestContext.Current.CancellationToken, (a, 2), (b, 2));

        Assert.Equal(20, counters.Succeeded);
        Assert.True(perWorker["b"] >= 17);
        for (var id = 1; id <= 20; id++)
            AssertCommitted(id);
    }

    [Fact]
    public async Task Remaining_work_fails_when_every_worker_is_down()
    {
        var a = Worker("a");
        a.Down = true;
        var (progress, counters, _) = await RunAsync(Items(5), false, TestContext.Current.CancellationToken, (a, 2));

        Assert.Equal(5, counters.Failed);
        Assert.All(progress.Units.Values, u => Assert.Equal(JobUnitOutcome.Failed, u.Outcome));
        Assert.Contains(progress.Units.Values, u => u.Message!.Contains("No live remote workers"));
    }

    [Fact]
    public async Task A_worker_that_cannot_reach_cove_hands_work_to_another()
    {
        var a = Worker("a");
        a.Behavior = _ => (TaskStates.Failed, ErrorCodes.SourceUnreachable, []);
        var b = Worker("b");
        var (_, counters, perWorker) = await RunAsync(Items(6), false, TestContext.Current.CancellationToken, (a, 1), (b, 1));

        Assert.Equal(6, counters.Succeeded);
        Assert.Equal(6, perWorker["b"]);
    }

    [Fact]
    public async Task A_generation_failure_is_final()
    {
        var a = Worker("a");
        a.Behavior = _ => (TaskStates.Failed, ErrorCodes.GenerationFailed, []);
        var (progress, counters, _) = await RunAsync(Items(2), false, TestContext.Current.CancellationToken, (a, 1));

        Assert.Equal(2, counters.Failed);
        Assert.Equal(2, a.Submitted);
        Assert.All(progress.Units.Values, u => Assert.StartsWith("a: error", u.Message));
    }

    [Fact]
    public async Task Sprite_spec_carries_the_item_sprite_width()
    {
        var a = Worker("a");
        var items = Items(2);
        items[1] = new WorkItem
        {
            VideoId = items[1].VideoId,
            Label = items[1].Label,
            CovePath = items[1].CovePath,
            SourcePath = items[1].SourcePath,
            Duration = items[1].Duration,
            Sprite = true,
            SpriteWidth = 320,
        };
        await RunAsync(items, false, TestContext.Current.CancellationToken, (a, 1));

        var sprites = a.Requests.ToDictionary(r => r.VideoId, r => r.Sprite!);
        Assert.Equal(SpriteSettings.DefaultWidth, sprites[1].FrameWidth);
        Assert.Equal(320, sprites[2].FrameWidth);
        Assert.Equal(SpriteSettings.MaxFrames, sprites[2].MaxFrames);
        Assert.Equal(new HashSet<string> { ArtifactKinds.Sprite, ArtifactKinds.Vtt }, a.Requests.Single(r => r.VideoId == 2).UploadUrls.Keys.ToHashSet());
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(320, true)]
    [InlineData(1920, true)]
    [InlineData(1921, false)]
    public void Sprite_width_bounds_match_the_worker_contract(int width, bool valid)
        => Assert.Equal(valid, SpriteSettings.IsValidWidth(width));

    [Fact]
    public async Task Partial_results_commit_only_what_succeeded()
    {
        var a = Worker("a");
        a.Behavior = _ => (TaskStates.Partial, null, [ArtifactKinds.Preview]);
        var (progress, counters, _) = await RunAsync(Items(1), false, TestContext.Current.CancellationToken, (a, 1));

        Assert.Equal(1, counters.Failed);
        Assert.Contains("preview failed (preview broke)", progress.Units["1"].Message);
        Assert.True(File.Exists(_paths.Cover(1)));
        Assert.True(File.Exists(_paths.Sprite(1)));
        Assert.False(File.Exists(_paths.Preview(1)));
    }

    [Fact]
    public async Task An_upload_that_does_not_match_what_the_worker_reported_is_not_committed()
    {
        var a = Worker("a");
        a.CorruptUploads = true;
        var (progress, counters, _) = await RunAsync(Items(1), false, TestContext.Current.CancellationToken, (a, 1));

        Assert.Equal(1, counters.Failed);
        Assert.Contains("upload was truncated", progress.Units["1"].Message);
        Assert.False(File.Exists(_paths.Cover(1)));
    }

    [Fact]
    public async Task Existing_files_survive_without_overwrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.Cover(1))!);
        File.WriteAllText(_paths.Cover(1), "mine");
        var a = Worker("a");

        await RunAsync(Items(1), false, TestContext.Current.CancellationToken, (a, 1));
        Assert.Equal("mine", File.ReadAllText(_paths.Cover(1)));

        await RunAsync(Items(1), true, TestContext.Current.CancellationToken, (a, 1));
        Assert.Equal("cover-1", File.ReadAllText(_paths.Cover(1)));
    }

    [Fact]
    public async Task Cancelling_cancels_the_running_tasks_and_revokes_their_access()
    {
        var a = Worker("a");
        a.Hang = true;
        using var cts = new CancellationTokenSource();
        var run = RunAsync(Items(4), false, cts.Token, (a, 2));

        while (Volatile.Read(ref a.Submitted) < 2)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, a.Cancelled.Count);
        Assert.All(a.Requests, r => Assert.Null(_access.Get(r.TaskId)));
    }

    private static List<WorkItem> PhashOnly(int count) => Items(count).Select(item => new WorkItem
    {
        VideoId = item.VideoId,
        Label = item.Label,
        CovePath = item.CovePath,
        SourcePath = item.SourcePath,
        FileId = item.FileId,
        Duration = item.Duration,
        Phash = true,
    }).ToList();

    [Fact]
    public async Task A_phash_is_stored_on_the_primary_file_and_needs_no_upload()
    {
        var a = Worker("a");
        var (_, counters, _) = await RunAsync(PhashOnly(3), false, TestContext.Current.CancellationToken, (a, 2));

        Assert.Equal(3, counters.Succeeded);
        Assert.All(a.Requests, r =>
        {
            Assert.Equal(new PhashSpec(25, 160), r.Phash);
            Assert.Empty(r.UploadUrls);
        });
        for (var id = 1; id <= 3; id++)
            Assert.Equal(FakeWorker.Phash(id), _fingerprints.Phashes[100 + id]);
        Assert.False(File.Exists(_paths.Cover(1)));
    }

    [Fact]
    public async Task A_failed_phash_fails_the_video_and_stores_nothing()
    {
        var a = Worker("a");
        a.Behavior = _ => (TaskStates.Partial, null, [ArtifactKinds.Phash]);
        var items = Items(1).Select(item => new WorkItem
        {
            VideoId = item.VideoId, Label = item.Label, CovePath = item.CovePath, SourcePath = item.SourcePath, FileId = item.FileId,
            Duration = item.Duration, Cover = true, Phash = true,
        }).ToList();
        var (progress, counters, _) = await RunAsync(items, false, TestContext.Current.CancellationToken, (a, 1));

        Assert.Equal(1, counters.Failed);
        Assert.Contains("phash failed (phash broke)", progress.Units["1"].Message);
        Assert.True(File.Exists(_paths.Cover(1)));
        Assert.Empty(_fingerprints.Phashes);
    }
}
