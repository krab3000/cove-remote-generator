using System.Runtime.InteropServices;

namespace RemoteHeavylifter.Worker.Bench;

/// <summary>System-wide CPU use between two readings.</summary>
public static partial class CpuMeter
{
    /// <summary>CPU time since boot, total and idle, in the platform's units.</summary>
    public static (long Total, long Idle) Read()
    {
        if (OperatingSystem.IsWindows())
        {
            GetSystemTimes(out var idle, out var kernel, out var user);
            return (kernel + user, idle); // kernel time includes idle time
        }
        if (!File.Exists("/proc/stat"))
            return (0, 0);
        // Linux: "cpu  user nice system idle iowait irq softirq steal …" in clock ticks.
        var ticks = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Take(8).Select(long.Parse).ToArray();
        return (ticks.Sum(), ticks[3] + ticks[4]);
    }

    /// <summary>Busy share (0–100) between two readings.</summary>
    public static double Busy((long Total, long Idle) before, (long Total, long Idle) after)
    {
        var total = after.Total - before.Total;
        return total <= 0 ? 0 : 100.0 * (total - (after.Idle - before.Idle)) / total;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);
}
