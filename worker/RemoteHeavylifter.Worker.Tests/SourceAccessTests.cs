using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Tasks;
using RemoteHeavylifter.Worker.Transport;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Sources read from the worker's filesystem when Cove sends a readable path, else over HTTP.</summary>
public sealed class SourceAccessTests : IDisposable
{
    private const string Token = "worker-token-0123456789abcdef";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _tmp = new();
    private readonly RecordingEngine _engine = new();
    private readonly CountingHandler _cove = new();
    private readonly TaskRunner _runner;

    public SourceAccessTests()
    {
        var options = new WorkerOptions { Token = Token, DataDir = _tmp.Path };
        var cove = new CoveHttpClient(new HttpClient(_cove), WorkerToken.Load(options));
        _runner = new TaskRunner(options, WorkerToken.Load(options), cove, new SourceCache(0, cove.GetRangeAsync),
            new SlotPool([new Slot(new SlotPlan(SlotIds.Cpu, SlotKinds.Cpu, null, 2), _engine, "libx264")]),
            NullLogger<TaskRunner>.Instance);
    }

    public void Dispose() => _tmp.Dispose();

    private string Video()
    {
        var path = Path.Combine(_tmp.Path, "v.mp4");
        File.WriteAllBytes(path, new byte[1234]);
        return path;
    }

    [Fact]
    public async Task A_readable_path_is_probed_as_a_file_without_asking_cove()
    {
        var path = Video();
        var result = await _runner.ProbeAsync(new ProbeParams("t1", "http://cove/source", path), Ct);

        Assert.True(result.Ok);
        Assert.Equal(SourceAccess.File, result.SourceAccess);
        var source = Assert.Single(_engine.Sources);
        Assert.Equal(Path.GetFullPath(path), source.Url);
        Assert.Empty(source.InputOptions);
        Assert.Equal(1234, source.Size);
        Assert.Equal(0, _cove.Requests);
    }

    [Fact]
    public async Task A_missing_path_falls_back_to_http()
    {
        var result = await _runner.ProbeAsync(new ProbeParams("t1", "http://cove/source", Path.Combine(_tmp.Path, "missing.mp4")), Ct);

        Assert.True(result.Ok);
        Assert.Equal(SourceAccess.Http, result.SourceAccess);
        Assert.Equal("http://cove/source", Assert.Single(_engine.Sources).Url);
        Assert.Equal(1, _cove.Requests);
    }

    [Fact]
    public async Task A_task_reads_its_file_without_http()
    {
        var path = Video();
        var request = new TaskRequest("t1", 7, "http://cove/source", 1234, 0, new Dictionary<string, string>(), null, null, null,
            SourcePath: path);
        var result = await _runner.RunAsync(request, new NullReporter(), Ct);

        // No steps were asked for, so nothing is produced; the source itself was read.
        Assert.Equal("no artifacts were produced", result.Error);
        Assert.Equal(Path.GetFullPath(path), Assert.Single(_engine.Sources).Url);
        Assert.Equal(0, _cove.Requests);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
            return Task.FromResult(response);
        }
    }

    private sealed class NullReporter : ITaskReporter
    {
        public Task ProgressAsync(TaskProgress progress) => Task.CompletedTask;
        public Task FinishedAsync(TaskResult result) => Task.CompletedTask;
    }

    private sealed class RecordingEngine : IMediaEngine
    {
        public List<MediaSource> Sources { get; } = [];
        public string Description => "recording";

        public Task<double> ProbeDurationAsync(MediaSource source, CancellationToken ct)
        {
            Sources.Add(source);
            return Task.FromResult(60.0);
        }

        public Task CoverAsync(MediaSource source, double seek, CoverSpec spec, string workDir, string output, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Image<Rgb24>?[]> ExtractFramesAsync(MediaSource source, IReadOnlyList<double> timestamps, int width, string? preFilter,
            string workDir, CancellationToken ct, bool keyframes = false) => throw new NotSupportedException();

        public Task PreviewAsync(MediaSource source, double duration, PreviewPlan plan, PreviewSpec spec, string workDir, string output,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
