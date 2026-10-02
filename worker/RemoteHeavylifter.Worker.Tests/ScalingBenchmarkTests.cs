using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>
/// Opt-in benchmark: how throughput scales with videos at once, per GPU setup (Intel Quick Sync; NVIDIA CUDA + NVENC on
/// two GPUs). For each count in HL_BENCH_COUNTS (default 1,2,4,8,12,16), max(8, 2×count) videos (HL_BENCH_FILE each
/// time) run the worker's steps (cover → preview → sprite → phash). Results are appended to HL_BENCH_OUT/results.txt;
/// without HL_BENCH_FILE and HL_BENCH_OUT the test skips.
/// </summary>
public sealed class ScalingBenchmarkTests
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [Fact]
    public async Task ScalingBench()
    {
        var clip = Environment.GetEnvironmentVariable("HL_BENCH_FILE");
        var dir = Environment.GetEnvironmentVariable("HL_BENCH_OUT");
        Assert.SkipWhen(clip is null || dir is null, "benchmark only");
        var counts = (Environment.GetEnvironmentVariable("HL_BENCH_COUNTS") ?? "1,2,4,8,12,16").Split(',').Select(int.Parse).ToArray();
        LibavEngines.Require();
        LibavLoader.TryLoad(null, out var info, out _);

        Directory.CreateDirectory(dir!);
        var log = Path.Combine(dir!, "results.txt");
        File.AppendAllText(log, $"{Path.GetFileName(clip)}{Environment.NewLine}");
        var ct = TestContext.Current.CancellationToken;
        var duration = await new LibavMediaEngine(info!, new LibavEngineOptions(null, 8, "libx264", []), Engines.Cli)
            .ProbeDurationAsync(Specs.Local(clip!), ct);

        var setups = new (string Name, string Accel, string[] Devices, string Encoder)[]
        {
            ("intel qsv", "qsv", [], "h264_qsv"),
            ("nvidia cuda+nvenc (2 GPUs)", "cuda", ["0", "1"], "h264_nvenc"),
        };
        foreach (var setup in setups)
        foreach (var parallel in counts)
        {
            var jobs = Math.Max(8, 2 * parallel);
            var engine = new LibavMediaEngine(info!, new LibavEngineOptions(null, parallel, setup.Encoder, []), Engines.Cli);
            var queue = new ConcurrentQueue<int>(Enumerable.Range(0, jobs));
            var failures = new ConcurrentBag<string>();
            var perVideo = new ConcurrentBag<double>();
            GetSystemTimes(out var idle0, out var kernel0, out var user0);
            var wall = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, parallel).Select(slot => Task.Run(async () =>
            {
                while (queue.TryDequeue(out var job))
                {
                    var device = setup.Devices.Length == 0 ? null : setup.Devices[job % setup.Devices.Length];
                    var source = Specs.Local(clip!).WithHardwareDecode(setup.Accel, device);
                    var work = Path.Combine(dir!, "work", job.ToString());
                    var video = Stopwatch.StartNew();
                    async Task Step(string step, Func<Task> run)
                    {
                        try
                        {
                            await run();
                        }
                        catch (MediaException ex)
                        {
                            failures.Add($"{step}: {ex.Message}");
                        }
                    }
                    await Step("cover", () => CoverGenerator.GenerateAsync(engine, source, duration, new CoverSpec(null, null), work, Path.Combine(work, "c.jpg"), ct));
                    await Step("preview", () => PreviewGenerator.GenerateAsync(engine, source, duration, Specs.Preview(), work, Path.Combine(work, "p.mp4"), ct));
                    await Step("sprite", () => SpriteGenerator.GenerateAsync(engine, source, duration, Specs.Sprite("x.jpg"), work, Path.Combine(work, "s.jpg"), Path.Combine(work, "s.vtt"), ct));
                    await Step("phash", () => PhashGenerator.GenerateAsync(engine, source, duration, new PhashSpec(), work, ct));
                    perVideo.Add(video.Elapsed.TotalSeconds);
                    Outputs.RemoveDirQuietly(work);
                }
            }, ct)));
            wall.Stop();
            GetSystemTimes(out var idle1, out var kernel1, out var user1);
            var total = (kernel1 - kernel0) + (user1 - user0);
            var busy = total - (idle1 - idle0);
            File.AppendAllText(log,
                $"  {setup.Name}, {parallel,2} at once ({jobs} videos): {jobs / wall.Elapsed.TotalMinutes,5:F1} videos/min, "
                + $"{perVideo.Average(),5:F1}s per video, CPU {100.0 * busy / total:F0}%, failures {failures.Count}"
                + Environment.NewLine + string.Concat(failures.Distinct().Take(2).Select(f => $"    {f}{Environment.NewLine}")));
        }
    }
}
