using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hardware;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Tasks;

namespace RemoteHeavylifter.Worker.Bench;

/// <summary>
/// <c>bench</c>: runs the worker's full pipeline (cover → preview → sprite → phash) with 1, 2, 3 … videos at once on each
/// slot pool (every GPU and the CPU) over Big Buck Bunny, and suggests how many slots each takes: the fewest videos at
/// once reaching 90% of its best throughput (more only make each video slower).
/// </summary>
public static class BenchRunner
{
    private const string ClipBase = "https://download.blender.org/demo/movies/BBB/";

    private static readonly IReadOnlyDictionary<string, string> Clips = new Dictionary<string, string>
    {
        ["1080p"] = "bbb_sunflower_1080p_30fps_normal.mp4",
        ["4k"] = "bbb_sunflower_2160p_30fps_normal.mp4",
    };

    /// <summary>The settings Cove generates with by default (the extension's PreviewSettings and SpriteSettings).</summary>
    private static readonly CoverSpec Cover = new(null, null);
    private static readonly PreviewSpec Preview = new(12, 0.75, "0", "0", "slow", false, 21, 640, null);
    private static readonly SpriteSpec Sprite = new(81, 160, null, "bench_sprite.jpg");
    private static readonly PhashSpec Phash = new();

    /// <summary>One measurement: <paramref name="Count"/> videos at once on a slot pool.</summary>
    public sealed record Run(string Clip, string Slot, int Count, double PerMinute, double SecondsPerVideo, double Cpu, int Failures);

    public static async Task<int> RunAsync(WorkerOptions options, SlotPool slots, ILogger log, CancellationToken ct)
    {
        var dir = Path.Combine(options.DataDir, "bench");
        Directory.CreateDirectory(dir);
        var runs = new List<Run>();
        foreach (var clip in options.BenchClips)
        {
            var path = await DownloadAsync(clip, dir, log, ct);
            var duration = await slots.Slots[0].Engine.ProbeDurationAsync(MediaSource.ForFile(path, new FileInfo(path).Length), ct);
            foreach (var slot in slots.Slots)
            foreach (var count in options.BenchCounts)
            {
                log.LogInformation("Bench {Clip} on {Slot}: {Count} at once …", clip, slot.Id, count);
                var run = await MeasureAsync(clip, slot, path, duration, count, Path.Combine(dir, "work"), ct);
                runs.Add(run);
                log.LogInformation("  {PerMinute:0.0} videos/min, {Seconds:0.0} s per video, CPU {Cpu:0}%, failures {Failures}",
                    run.PerMinute, run.SecondsPerVideo, run.Cpu, run.Failures);
            }
        }
        Console.WriteLine();
        Console.WriteLine(Report(runs, slots));
        return 0;
    }

    private static async Task<Run> MeasureAsync(string clip, Slot slot, string path, double duration, int count, string workRoot, CancellationToken ct)
    {
        var failures = new ConcurrentBag<string>();
        var perVideo = new ConcurrentBag<double>();
        var cpu0 = CpuMeter.Read();
        var wall = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, count).Select(job => Task.Run(async () =>
        {
            var source = slot.Decode(MediaSource.ForFile(path, new FileInfo(path).Length));
            var work = Path.Combine(workRoot, $"{slot.Id}-{job}");
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
            var engine = slot.Engine;
            await Step("cover", () => CoverGenerator.GenerateAsync(engine, source, duration, Cover, work, Path.Combine(work, "c.jpg"), ct));
            await Step("preview", () => PreviewGenerator.GenerateAsync(engine, source, duration, Preview, work, Path.Combine(work, "p.mp4"), ct));
            await Step("sprite", () => SpriteGenerator.GenerateAsync(engine, source, duration, Sprite, work, Path.Combine(work, "s.jpg"),
                Path.Combine(work, "s.vtt"), ct));
            await Step("phash", () => PhashGenerator.GenerateAsync(engine, source, duration, Phash, work, ct));
            perVideo.Add(video.Elapsed.TotalSeconds);
            Outputs.RemoveDirQuietly(work);
        }, ct)));
        wall.Stop();
        return new Run(clip, slot.Id, count, count / wall.Elapsed.TotalMinutes, perVideo.Average(), CpuMeter.Busy(cpu0, CpuMeter.Read()),
            failures.Count);
    }

    /// <summary>The fewest videos at once whose throughput reaches 90% of the best; a run with failures never counts.</summary>
    public static int Suggest(IEnumerable<(int Count, double PerMinute, int Failures)> runs)
    {
        var clean = runs.Where(r => r.Failures == 0).ToList();
        if (clean.Count == 0)
            return 0;
        var best = clean.Max(r => r.PerMinute);
        return clean.Where(r => r.PerMinute >= 0.9 * best).Min(r => r.Count);
    }

    internal static string Report(IReadOnlyList<Run> runs, SlotPool slots)
    {
        var lines = new List<string> { "clip    slot      at once  videos/min  s/video   CPU  failures" };
        lines.AddRange(runs.Select(r => string.Create(CultureInfo.InvariantCulture,
            $"{r.Clip,-7} {r.Slot,-9} {r.Count,7}  {r.PerMinute,10:0.0}  {r.SecondsPerVideo,7:0.0}  {r.Cpu,3:0}%  {r.Failures,8}")));
        lines.Add("");

        var flags = new List<string>();
        foreach (var slot in slots.Slots)
        {
            // The busiest clip decides: a slot count fine for 1080p may be too many for 4K and the other way round.
            var suggested = runs.Where(r => r.Slot == slot.Id).GroupBy(r => r.Clip)
                .Select(g => Suggest(g.Select(r => (r.Count, r.PerMinute, r.Failures)))).DefaultIfEmpty(0).Max();
            lines.Add($"{slot.Id}: {(suggested == 0 ? "every run failed; leave it off" : $"{suggested} at once")}");
            flags.Add(slot.Plan.Gpu switch
            {
                { Vendor: GpuVendors.Intel } gpu => $"--hv-intel-slots={gpu.Index},{suggested}",
                { Vendor: GpuVendors.Nvidia } gpu => $"--hv-nvidia-slots={gpu.Index},{suggested}",
                { Vendor: GpuVendors.Apple } gpu => $"--hv-apple-slots={gpu.Index},{suggested}",
                _ => $"--cpu-slots={suggested}",
            });
        }
        lines.Add("");
        lines.Add("Suggested: " + string.Join(" ", flags));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The clip, downloaded and unzipped into <paramref name="dir"/> once.</summary>
    private static async Task<string> DownloadAsync(string clip, string dir, ILogger log, CancellationToken ct)
    {
        var name = Clips[clip];
        var path = Path.Combine(dir, name);
        if (File.Exists(path))
            return path;
        var zip = path + ".zip";
        var url = ClipBase + name + ".zip";
        log.LogInformation("Downloading {Url}", url);
        using (var http = new HttpClient { Timeout = TimeSpan.FromHours(1) })
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(zip + ".part");
            var buffer = new byte[1 << 20];
            long done = 0, reported = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (length is > 0 && done - reported >= length / 10)
                {
                    reported = done;
                    log.LogInformation("  {Percent:0}% of {Megabytes} MB", 100.0 * done / length.Value, length.Value / (1024 * 1024));
                }
            }
        }
        File.Move(zip + ".part", zip, overwrite: true);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name == name) ?? throw new InvalidDataException($"{zip} holds no {name}");
            entry.ExtractToFile(path + ".part", overwrite: true);
        }
        File.Move(path + ".part", path, overwrite: true);
        File.Delete(zip);
        return path;
    }
}
