using System.Globalization;
using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Performance;

/// One host reading, already turned into rates/percentages over the
/// interval since the previous reading. Fields are null when the source
/// is not readable (non-Linux dev box, cgroup v1 without a file, …).
public sealed record HostSample(
    double? CpuPct,
    double? StealPct,
    double? IoWaitPct,
    double? Load1,
    int? HostCores,
    double? MemTotalMb,
    double? MemAvailableMb,
    double? SwapUsedMb,
    double? SwapInPages,
    double? SwapOutPages,
    double? DiskAwaitMs,
    double? DiskUtilPct,
    double? CgroupThrottledPct,
    double? CgroupMemMb,
    double? CgroupMemLimitMb,
    long? OomKills,
    double? RootFreePct,
    double? BlobFreePct,
    double? RootFreeGb,
    double? BlobFreeGb);

/// Reads the Linux host through /proc and /sys/fs/cgroup. Inside the app
/// container /proc/stat, /proc/meminfo, /proc/loadavg and /proc/diskstats
/// are host-wide (no lxcfs), while cgroup files describe the container —
/// the dashboard labels each value accordingly. The parsers are pure
/// functions so they can be tested with fixture text on any OS.
public sealed partial class HostMetricsReader
{
    private readonly string _root;
    private CpuTimes? _prevCpu;
    private (long In, long Out)? _prevSwap;
    private DiskTotals? _prevDisk;
    private (long Periods, long Throttled)? _prevCgroup;
    private long _prevTimestampMs;

    public HostMetricsReader(string root = "/") => _root = root;

    public static bool IsSupported => OperatingSystem.IsLinux();

    public HostSample? Read(string? blobRoot)
    {
        if (!IsSupported && _root == "/") return null;
        var nowMs = Environment.TickCount64;
        var elapsedMs = _prevTimestampMs == 0 ? 0 : nowMs - _prevTimestampMs;
        _prevTimestampMs = nowMs;

        double? cpu = null, steal = null, iowait = null;
        int? cores = null;
        var stat = ReadText("proc/stat");
        if (stat is not null)
        {
            var now = ParseCpu(stat);
            cores = CountCores(stat);
            if (now is not null && _prevCpu is not null)
            {
                var (c, s, w) = CpuPercentages(_prevCpu.Value, now.Value);
                cpu = c; steal = s; iowait = w;
            }
            _prevCpu = now;
        }

        var load = ParseLoad(ReadText("proc/loadavg"));
        var mem = ParseMeminfo(ReadText("proc/meminfo"));

        double? swapIn = null, swapOut = null;
        var vm = ParseVmstat(ReadText("proc/vmstat"));
        if (vm is not null)
        {
            if (_prevSwap is not null)
            {
                swapIn = Math.Max(0, vm.Value.In - _prevSwap.Value.In);
                swapOut = Math.Max(0, vm.Value.Out - _prevSwap.Value.Out);
            }
            _prevSwap = vm;
        }

        double? diskAwait = null, util = null;
        var disk = ParseDiskstats(ReadText("proc/diskstats"));
        if (disk is not null)
        {
            if (_prevDisk is not null && elapsedMs > 0)
            {
                (diskAwait, util) = DiskRates(_prevDisk.Value, disk.Value, elapsedMs);
            }
            _prevDisk = disk;
        }

        double? throttled = null;
        var cg = ParseCgroupCpu(ReadText("sys/fs/cgroup/cpu.stat") ?? ReadText("sys/fs/cgroup/cpu/cpu.stat"));
        if (cg is not null)
        {
            if (_prevCgroup is not null)
            {
                var dp = cg.Value.Periods - _prevCgroup.Value.Periods;
                var dt = cg.Value.Throttled - _prevCgroup.Value.Throttled;
                throttled = dp > 0 ? Math.Clamp(100.0 * dt / dp, 0, 100) : 0;
            }
            _prevCgroup = cg;
        }

        var cgMem = ParseLong(ReadText("sys/fs/cgroup/memory.current") ?? ReadText("sys/fs/cgroup/memory/memory.usage_in_bytes"));
        var cgLimit = ParseLong(ReadText("sys/fs/cgroup/memory.max") ?? ReadText("sys/fs/cgroup/memory/memory.limit_in_bytes"));
        // cgroup v1 reports "no limit" as a huge number.
        if (cgLimit is > (1L << 50)) cgLimit = null;
        var oom = ParseKeyValue(ReadText("sys/fs/cgroup/memory.events"), "oom_kill");

        var (rootFreePct, rootFreeGb) = FreeSpace("/");
        var (blobFreePct, blobFreeGb) = string.IsNullOrWhiteSpace(blobRoot) ? (null, null) : FreeSpace(blobRoot);

        return new HostSample(
            cpu, steal, iowait, load, cores,
            mem?.TotalKb / 1024.0, mem?.AvailableKb / 1024.0,
            mem is null ? null : Math.Max(0, mem.Value.SwapTotalKb - mem.Value.SwapFreeKb) / 1024.0,
            swapIn, swapOut, diskAwait, util, throttled,
            cgMem / 1024.0 / 1024.0, cgLimit / 1024.0 / 1024.0, oom,
            rootFreePct, blobFreePct, rootFreeGb, blobFreeGb);
    }

    private string? ReadText(string relative)
    {
        try
        {
            var path = Path.Combine(_root, relative);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static (double? Pct, double? Gb) FreeSpace(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return (null, null);
            var drive = new DriveInfo(path);
            if (!drive.IsReady || drive.TotalSize <= 0) return (null, null);
            return (100.0 * drive.AvailableFreeSpace / drive.TotalSize, drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0);
        }
        catch
        {
            return (null, null);
        }
    }

    // ---------------------------------------------------------------- parsers

    public readonly record struct CpuTimes(long User, long Nice, long System, long Idle, long IoWait, long Irq, long SoftIrq, long Steal)
    {
        public long Total => User + Nice + System + Idle + IoWait + Irq + SoftIrq + Steal;
    }

    public static CpuTimes? ParseCpu(string? stat)
    {
        if (stat is null) return null;
        foreach (var line in stat.Split('\n'))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal)) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 9) return null;
            long F(int i) => long.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return new CpuTimes(F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8));
        }
        return null;
    }

    public static int? CountCores(string? stat)
    {
        if (stat is null) return null;
        var n = stat.Split('\n').Count(l => CpuCoreLine().IsMatch(l));
        return n == 0 ? null : n;
    }

    public static (double Cpu, double Steal, double IoWait) CpuPercentages(CpuTimes prev, CpuTimes now)
    {
        var total = now.Total - prev.Total;
        if (total <= 0) return (0, 0, 0);
        var idle = (now.Idle - prev.Idle) + (now.IoWait - prev.IoWait);
        var busy = total - idle;
        return (
            Math.Clamp(100.0 * busy / total, 0, 100),
            Math.Clamp(100.0 * (now.Steal - prev.Steal) / total, 0, 100),
            Math.Clamp(100.0 * (now.IoWait - prev.IoWait) / total, 0, 100));
    }

    public static double? ParseLoad(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public readonly record struct MemInfo(long TotalKb, long AvailableKb, long SwapTotalKb, long SwapFreeKb);

    public static MemInfo? ParseMeminfo(string? text)
    {
        if (text is null) return null;
        long total = -1, available = -1, swapTotal = 0, swapFree = 0;
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();
            var space = value.IndexOf(' ');
            if (space > 0) value = value[..space];
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kb)) continue;
            switch (key)
            {
                case "MemTotal": total = kb; break;
                case "MemAvailable": available = kb; break;
                case "SwapTotal": swapTotal = kb; break;
                case "SwapFree": swapFree = kb; break;
            }
        }
        return total < 0 || available < 0 ? null : new MemInfo(total, available, swapTotal, swapFree);
    }

    public static (long In, long Out)? ParseVmstat(string? text)
    {
        if (text is null) return null;
        long? pin = ParseKeyValue(text, "pswpin"), pout = ParseKeyValue(text, "pswpout");
        return pin is null || pout is null ? null : (pin.Value, pout.Value);
    }

    public readonly record struct DiskTotals(long Ios, long IoMs, long BusyMsMax, IReadOnlyDictionary<string, long> BusyMsPerDevice);

    public static DiskTotals? ParseDiskstats(string? text)
    {
        if (text is null) return null;
        long ios = 0, ioMs = 0;
        var busy = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 14) continue;
            var name = p[2];
            if (!PhysicalDisk().IsMatch(name)) continue;
            long F(int i) => long.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
            ios += F(3) + F(7);
            ioMs += F(6) + F(10);
            busy[name] = F(12);
        }
        return busy.Count == 0 ? null : new DiskTotals(ios, ioMs, busy.Values.Max(), busy);
    }

    /// Average latency per I/O and the busiest device's utilisation.
    public static (double? AwaitMs, double? UtilPct) DiskRates(DiskTotals prev, DiskTotals now, long elapsedMs)
    {
        var dIos = now.Ios - prev.Ios;
        var dMs = now.IoMs - prev.IoMs;
        double? awaitMs = dIos > 0 ? (double)dMs / dIos : 0;
        double maxUtil = 0;
        foreach (var (device, busyNow) in now.BusyMsPerDevice)
        {
            if (!prev.BusyMsPerDevice.TryGetValue(device, out var busyPrev)) continue;
            maxUtil = Math.Max(maxUtil, Math.Clamp(100.0 * (busyNow - busyPrev) / elapsedMs, 0, 100));
        }
        return (awaitMs, maxUtil);
    }

    /// cgroup v2 (nr_periods / nr_throttled) and v1 (same keys) cpu.stat.
    public static (long Periods, long Throttled)? ParseCgroupCpu(string? text)
    {
        if (text is null) return null;
        var periods = ParseKeyValue(text, "nr_periods");
        var throttled = ParseKeyValue(text, "nr_throttled");
        return periods is null || throttled is null ? null : (periods.Value, throttled.Value);
    }

    public static long? ParseKeyValue(string? text, string key)
    {
        if (text is null) return null;
        foreach (var line in text.Split('\n'))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[0] == key &&
                long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return v;
        }
        return null;
    }

    public static long? ParseLong(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"^cpu\d+ ")]
    private static partial Regex CpuCoreLine();

    // Whole physical disks only (no partitions, loop, ram or device-mapper
    // devices, which would double-count the same I/O).
    [GeneratedRegex(@"^(?:(?:sd|vd|xvd|hd)[a-z]+|nvme\d+n\d+|mmcblk\d+)$")]
    private static partial Regex PhysicalDisk();
}
