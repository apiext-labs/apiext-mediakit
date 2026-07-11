using System.Diagnostics;

namespace Apiext.MediaKit;

public sealed class HostStatsService
{
    private static readonly Process Process = Process.GetCurrentProcess();
    private static DateTime _lastCpuTime = DateTime.UtcNow;
    private static TimeSpan _lastTotalProcessor = Process.TotalProcessorTime;
    private static double _lastCpuPercent;

    public HostStatsDto GetStats()
    {
        Process.Refresh();
        var now = DateTime.UtcNow;
        var cpu = Process.TotalProcessorTime;
        var wall = (now - _lastCpuTime).TotalMilliseconds;
        if (wall > 200)
        {
            var cpuMs = (cpu - _lastTotalProcessor).TotalMilliseconds;
            _lastCpuPercent = Math.Clamp(cpuMs / (Environment.ProcessorCount * wall) * 100.0, 0, 100);
            _lastCpuTime = now;
            _lastTotalProcessor = cpu;
        }

        return new HostStatsDto(
            Environment.MachineName,
            Environment.OSVersion.ToString(),
            Environment.ProcessorCount,
            Math.Round(_lastCpuPercent, 1),
            Process.WorkingSet64,
            GC.GetTotalMemory(false),
            (long)(DateTime.UtcNow - Process.StartTime.ToUniversalTime()).TotalSeconds,
            DateTime.UtcNow);
    }
}

public sealed record HostStatsDto(
    string MachineName,
    string OsDescription,
    int ProcessorCount,
    double ProcessCpuPercent,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    long UptimeSeconds,
    DateTime SampledAtUtc);
