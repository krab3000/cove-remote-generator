using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Contract;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Remote;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Tests;

/// <summary>An in-memory generation server.</summary>
internal sealed class FakeServer(string name, int capacity = 8)
{
    public ServerDefinition Definition { get; } = new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        BaseUrl = $"http://{name}",
        ApiKey = "key",
        Mappings = [new PathMapping("/cove", $"/{name}")],
    };

    public int Capacity { get; } = capacity;
    public volatile bool Down;
    public int InFlight;
    public int MaxInFlight;
    public int Submitted;
    public readonly ConcurrentBag<string> Deleted = [];
    public readonly ConcurrentDictionary<string, (RemoteTaskRequest Request, RemoteTaskStatus Final)> Tasks = new();

    /// <summary>Decides a task's final status; defaults to every requested artifact succeeding.</summary>
    public Func<RemoteTaskRequest, (string Status, string? ErrorCode, string[] FailedArtifacts)> Behavior { get; set; }
        = _ => (RemoteTaskStates.Succeeded, null, []);

    /// <summary>Called on submit; throw to simulate an outage.</summary>
    public Action<RemoteTaskRequest>? OnSubmit { get; set; }

    public static byte[] Content(string kind, int videoId) => Encoding.UTF8.GetBytes($"{kind}-{videoId}");
}

internal sealed class FakeClient(FakeServer server) : IRemoteClient
{
    private void ThrowIfDown()
    {
        if (server.Down)
            throw new RemoteException($"{server.Definition.Name} is unreachable", transient: true);
    }

    public Task<RemoteInfo> GetInfoAsync(TimeSpan timeout, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult(new RemoteInfo(1, "test", "ffmpeg test", "libx264", server.Capacity, 0, 0, 100, 1L << 40, ["/"]));
    }

    public Task<IReadOnlyList<PathCheckResult>> CheckPathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PathCheckResult>>(paths.Select(p => new PathCheckResult(p, true, true, true)).ToList());

    public Task<RemoteTaskStatus> SubmitAsync(RemoteTaskRequest request, CancellationToken ct)
    {
        ThrowIfDown();
        server.OnSubmit?.Invoke(request);
        Interlocked.Increment(ref server.Submitted);
        var now = Interlocked.Increment(ref server.InFlight);
        int max;
        while (now > (max = Volatile.Read(ref server.MaxInFlight)) && Interlocked.CompareExchange(ref server.MaxInFlight, now, max) != max)
        {
        }

        var id = Guid.NewGuid().ToString("N");
        var (status, code, failed) = server.Behavior(request);
        var kinds = new List<string>();
        if (request.Cover is not null) kinds.Add(ArtifactKinds.Cover);
        if (request.Preview is not null) kinds.Add(ArtifactKinds.Preview);
        if (request.Sprite is not null) kinds.AddRange([ArtifactKinds.Sprite, ArtifactKinds.Vtt]);
        var artifacts = kinds.ToDictionary(k => k, k =>
        {
            if (status == RemoteTaskStates.Failed || failed.Contains(k))
                return new RemoteArtifact("failed", null, null, $"{k} broke");
            var bytes = FakeServer.Content(k, request.VideoId);
            return new RemoteArtifact("succeeded", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), null);
        });
        var final = new RemoteTaskStatus(id, request.ClientTaskId, request.VideoId, status, 1, code is null ? null : "error", code,
            artifacts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        server.Tasks[id] = (request, final);
        return Task.FromResult(final with { Status = RemoteTaskStates.Queued, Artifacts = new Dictionary<string, RemoteArtifact>() });
    }

    public Task<RemoteTaskStatus?> GetTaskAsync(string taskId, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult(server.Tasks.TryGetValue(taskId, out var task) ? task.Final : null);
    }

    public async Task<DownloadResult> DownloadArtifactAsync(string taskId, string kind, string destinationPath, CancellationToken ct)
    {
        ThrowIfDown();
        var bytes = FakeServer.Content(kind, server.Tasks[taskId].Request.VideoId);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, bytes, ct);
        return new DownloadResult(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public Task DeleteTaskAsync(string taskId, CancellationToken ct)
    {
        if (server.Tasks.TryRemove(taskId, out _))
            Interlocked.Decrement(ref server.InFlight);
        server.Deleted.Add(taskId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeClients(params FakeServer[] servers) : IRemoteClientFactory
{
    public IRemoteClient Create(ServerDefinition server) => new FakeClient(servers.Single(s => s.Definition.Id == server.Id));
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
        [TimeSpan.FromMilliseconds(1)],
        ReviveInterval: TimeSpan.FromMilliseconds(20),
        AllDeadGrace: TimeSpan.FromMilliseconds(60),
        DeferDelay: TimeSpan.FromMilliseconds(2),
        QueuedTimeout: TimeSpan.FromSeconds(10),
        RunningTimeout: TimeSpan.FromSeconds(10),
        CleanupTimeout: TimeSpan.FromSeconds(1),
        MaxAttempts: 3,
        FailuresBeforeDead: 2,
        PollErrorsBeforeGivingUp: 2);

    private readonly string _root = Directory.CreateTempSubdirectory("rh-run-").FullName;
    private readonly GeneratedPaths _paths;

    public RunExecutorTests() => _paths = new GeneratedPaths(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static List<WorkItem> Items(int count, params FakeServer[] reachableFrom) => Enumerable.Range(1, count)
        .Select(id => new WorkItem
        {
            VideoId = id,
            Label = $"video {id}",
            CovePath = $"/cove/v{id}.mp4",
            Duration = 60,
            Cover = true,
            Preview = true,
            Sprite = true,
            PreviewScale = "scale=640:-2",
            RemotePaths = reachableFrom.ToDictionary(s => s.Definition.Id, s => $"/{s.Definition.Name}/v{id}.mp4"),
        })
        .ToList();

    private async Task<(RecordingProgress Progress, RunCounters Counters, IReadOnlyDictionary<string, int> PerServer)> RunAsync(
        IReadOnlyList<WorkItem> items, bool overwrite, CancellationToken ct, params (FakeServer Server, int Slots)[] servers)
    {
        var factory = new FakeClients(servers.Select(s => s.Server).ToArray());
        var executor = new RunExecutor(factory, new HealthMonitor(factory), Fast, TimeProvider.System, NullLogger.Instance);
        var progress = new RecordingProgress();
        var counters = new RunCounters { Total = items.Count };
        var run = new RunContext("run1", _paths, new PreviewSettings(12, 0.75, "0", "0", "slow", false), overwrite);
        var perServer = await executor.ExecuteAsync(
            servers.Select(s => new LiveServer(s.Server.Definition, s.Slots)).ToList(), items, run, progress, counters, ct);
        return (progress, counters, perServer);
    }

    private void AssertCommitted(int id)
    {
        Assert.Equal("cover-" + id, File.ReadAllText(_paths.Cover(id)));
        Assert.Equal("preview-" + id, File.ReadAllText(_paths.Preview(id)));
        Assert.Equal("sprite-" + id, File.ReadAllText(_paths.Sprite(id)));
        Assert.Equal("vtt-" + id, File.ReadAllText(_paths.SpriteVtt(id)));
    }

    [Fact]
    public async Task Distributes_within_each_servers_slots_and_commits_everything()
    {
        var a = new FakeServer("a");
        var b = new FakeServer("b");
        var (progress, counters, perServer) = await RunAsync(Items(40, a, b), false, TestContext.Current.CancellationToken, (a, 2), (b, 3));

        Assert.Equal(40, counters.Succeeded);
        Assert.All(progress.Units.Values, u => Assert.Equal(JobUnitOutcome.Succeeded, u.Outcome));
        Assert.InRange(a.MaxInFlight, 1, 2);
        Assert.InRange(b.MaxInFlight, 1, 3);
        Assert.Equal(40, perServer["a"] + perServer["b"]);
        Assert.Empty(a.Tasks);
        Assert.Empty(b.Tasks);
        for (var id = 1; id <= 40; id++)
            AssertCommitted(id);
        Assert.False(Directory.Exists(Path.Combine(_paths.TempRoot, "run1", "1")));
    }

    [Fact]
    public async Task Work_moves_to_the_other_server_when_one_dies()
    {
        var a = new FakeServer("a");
        var b = new FakeServer("b");
        a.OnSubmit = _ =>
        {
            if (Volatile.Read(ref a.Submitted) >= 3)
                a.Down = true;
        };

        var (_, counters, perServer) = await RunAsync(Items(20, a, b), false, TestContext.Current.CancellationToken, (a, 2), (b, 2));

        Assert.Equal(20, counters.Succeeded);
        Assert.True(perServer["b"] >= 17);
        for (var id = 1; id <= 20; id++)
            AssertCommitted(id);
    }

    [Fact]
    public async Task Remaining_work_fails_when_every_server_is_down()
    {
        var a = new FakeServer("a") { Down = true };
        var (progress, counters, _) = await RunAsync(Items(5, a), false, TestContext.Current.CancellationToken, (a, 2));

        Assert.Equal(5, counters.Failed);
        Assert.All(progress.Units.Values, u => Assert.Equal(JobUnitOutcome.Failed, u.Outcome));
        Assert.Contains(progress.Units.Values, u => u.Message!.Contains("No live remote generation servers") || u.Message.Contains("Gave up"));
    }

    [Fact]
    public async Task A_server_that_cannot_find_the_file_hands_it_to_another()
    {
        var a = new FakeServer("a") { Behavior = _ => (RemoteTaskStates.Failed, "source_not_found", []) };
        var b = new FakeServer("b");
        var (_, counters, perServer) = await RunAsync(Items(6, a, b), false, TestContext.Current.CancellationToken, (a, 1), (b, 1));

        Assert.Equal(6, counters.Succeeded);
        Assert.Equal(6, perServer["b"]);
    }

    [Fact]
    public async Task Source_missing_everywhere_fails_with_the_reasons()
    {
        var a = new FakeServer("a") { Behavior = _ => (RemoteTaskStates.Failed, "source_not_found", []) };
        var (progress, counters, _) = await RunAsync(Items(2, a), false, TestContext.Current.CancellationToken, (a, 1));

        Assert.Equal(2, counters.Failed);
        Assert.All(progress.Units.Values, u => Assert.StartsWith("a: error", u.Message));
    }

    [Fact]
    public async Task Partial_results_commit_only_what_succeeded()
    {
        var a = new FakeServer("a") { Behavior = _ => (RemoteTaskStates.Partial, null, [ArtifactKinds.Preview]) };
        var (progress, counters, _) = await RunAsync(Items(1, a), false, TestContext.Current.CancellationToken, (a, 1));

        Assert.Equal(1, counters.Failed);
        Assert.Contains("preview failed (preview broke)", progress.Units["1"].Message);
        Assert.True(File.Exists(_paths.Cover(1)));
        Assert.True(File.Exists(_paths.Sprite(1)));
        Assert.False(File.Exists(_paths.Preview(1)));
    }

    [Fact]
    public async Task Existing_files_survive_without_overwrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.Cover(1))!);
        File.WriteAllText(_paths.Cover(1), "mine");
        var a = new FakeServer("a");

        await RunAsync(Items(1, a), false, TestContext.Current.CancellationToken, (a, 1));
        Assert.Equal("mine", File.ReadAllText(_paths.Cover(1)));

        await RunAsync(Items(1, a), true, TestContext.Current.CancellationToken, (a, 1));
        Assert.Equal("cover-1", File.ReadAllText(_paths.Cover(1)));
    }

    [Fact]
    public async Task Cancelling_deletes_the_remote_tasks()
    {
        var a = new FakeServer("a") { Behavior = _ => (RemoteTaskStates.Running, null, []) };
        using var cts = new CancellationTokenSource();
        var run = RunAsync(Items(4, a), false, cts.Token, (a, 2));

        while (Volatile.Read(ref a.Submitted) < 2)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(a.Tasks);
        Assert.Equal(2, a.Deleted.Count);
    }
}
