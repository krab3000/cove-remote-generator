using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Remote;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Tests;

/// <summary>
/// End-to-end against a running generation server. Opt in with:
///   HL_IT_URL=http://127.0.0.1:8751  HL_IT_KEY=...  HL_IT_MEDIA_ROOT=&lt;server media root&gt;  HL_IT_CLIP=&lt;file name in it&gt;
/// </summary>
public sealed class LiveServerTests : IDisposable
{
    private sealed class HttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = Timeout.InfiniteTimeSpan };
    }

    private readonly string _root = Directory.CreateTempSubdirectory("rh-live-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Generates_and_commits_every_artifact_through_a_real_server()
    {
        var url = Environment.GetEnvironmentVariable("HL_IT_URL");
        Assert.SkipWhen(string.IsNullOrEmpty(url), "HL_IT_URL is not set");
        var mediaRoot = Environment.GetEnvironmentVariable("HL_IT_MEDIA_ROOT")!;
        var clip = Environment.GetEnvironmentVariable("HL_IT_CLIP")!;

        var server = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "live",
            BaseUrl = url!,
            ApiKey = Environment.GetEnvironmentVariable("HL_IT_KEY")!,
            // Cove calls the library "/library"; the server sees the same folder at its media root.
            Mappings = [new PathMapping("/library", mediaRoot)],
        };
        var factory = new HttpRemoteClientFactory(new HttpClients());
        var monitor = new HealthMonitor(factory);
        var health = await monitor.ProbeAsync(server, refresh: true, TestContext.Current.CancellationToken);
        Assert.True(health.Live, health.Error);

        var paths = new GeneratedPaths(_root);
        var item = new WorkItem
        {
            VideoId = 4242,
            Label = "live clip",
            CovePath = $"/library/{clip}",
            Duration = 0, // let the server probe it
            Cover = true,
            Preview = true,
            Sprite = true,
            PreviewScale = "scale=640:-2",
            RemotePaths = new() { [server.Id] = PathMapper.Map($"/library/{clip}", server.Mappings)! },
        };
        var progress = new RecordingProgress();
        var counters = new RunCounters { Total = 1 };
        var executor = new RunExecutor(factory, monitor, CoordinatorTimings.Default, TimeProvider.System, NullLogger.Instance);
        await executor.ExecuteAsync(
            [new LiveServer(server, 1)],
            [item],
            new RunContext("live", paths, new PreviewSettings(12, 0.75, "0", "0", "ultrafast", false), Overwrite: false),
            progress,
            counters,
            TestContext.Current.CancellationToken);

        Assert.Equal((JobUnitOutcome: Cove.Core.Interfaces.JobUnitOutcome.Succeeded, Message: (string?)null), progress.Units["4242"]);
        Assert.Equal([0xFF, 0xD8], File.ReadAllBytes(paths.Cover(4242))[..2]);
        Assert.Equal([0xFF, 0xD8], File.ReadAllBytes(paths.Sprite(4242))[..2]);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(paths.Preview(4242)), 4, 4));
        var vtt = File.ReadAllText(paths.SpriteVtt(4242));
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("4242_sprite.jpg#xywh=0,0,", vtt);
        Assert.False(Directory.Exists(Path.Combine(paths.TempRoot, "live", "4242")));
    }
}
