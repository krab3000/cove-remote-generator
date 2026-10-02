using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>
/// Opt-in benchmark: how throughput scales with videos at once, per GPU setup (Intel Quick Sync; NVIDIA CUDA + NVENC on
/// two GPUs). For each count in HL_BENCH_COUNTS (default 1,2,4,8,12,16), HL_BENCH_JOBS or max(8, 2×count) videos (HL_BENCH_FILE each
/// time) run the worker's steps (cover → preview → sprite → phash). HL_BENCH_SETUPS (qsv, cuda; default both) picks
/// the setups. Results are appended to HL_BENCH_OUT/results.txt; without HL_BENCH_FILE and HL_BENCH_OUT the test skips.
/// </summary>
public sealed class ScalingBenchmarkTests
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>CPU time since boot, total and idle, in the platform's units.</summary>
    private static (long Total, long Idle) CpuTimes()
    {
        if (OperatingSystem.IsWindows())
        {
            GetSystemTimes(out var idle, out var kernel, out var user);
            return (kernel + user, idle); // kernel time includes idle time
        }
        // Linux: "cpu  user nice system idle iowait irq softirq steal …" in clock ticks.
        var ticks = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Take(8).Select(long.Parse).ToArray();
        return (ticks.Sum(), ticks[3] + ticks[4]);
    }

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
        var picked = Environment.GetEnvironmentVariable("HL_BENCH_SETUPS")?.Split(',');
        foreach (var setup in setups.Where(s => picked is null || picked.Contains(s.Accel)))
        foreach (var parallel in counts)
        {
            var jobs = int.TryParse(Environment.GetEnvironmentVariable("HL_BENCH_JOBS"), out var fixedJobs) ? fixedJobs : Math.Max(8, 2 * parallel);
            var engine = new LibavMediaEngine(info!, new LibavEngineOptions(null, parallel, setup.Encoder, []), Engines.Cli);
            var queue = new ConcurrentQueue<int>(Enumerable.Range(0, jobs));
            var failures = new ConcurrentBag<string>();
            var perVideo = new ConcurrentBag<double>();
            var cpu0 = CpuTimes();
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
            var cpu1 = CpuTimes();
            var total = cpu1.Total - cpu0.Total;
            var busy = total - (cpu1.Idle - cpu0.Idle);
            File.AppendAllText(log,
                $"  {setup.Name}, {parallel,2} at once ({jobs} videos): {jobs / wall.Elapsed.TotalMinutes,5:F1} videos/min, "
                + $"{perVideo.Average(),5:F1}s per video, CPU {100.0 * busy / total:F0}%, failures {failures.Count}"
                + Environment.NewLine + string.Concat(failures.Distinct().Take(2).Select(f => $"    {f}{Environment.NewLine}")));
        }
    }
}
