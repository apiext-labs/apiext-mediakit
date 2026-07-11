using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Apiext.MediaKit;

/// <summary>IP allow-list + simple per-IP sliding window rate limit for webhook ingress.</summary>
public sealed class WebhookProtection
{
    private readonly ConcurrentDictionary<string, WindowCounter> _windows = new(StringComparer.Ordinal);
    private readonly object _cleanupLock = new();
    private DateTime _lastCleanupUtc = DateTime.UtcNow;

    public bool IsIpAllowed(string? remoteIp, IReadOnlyList<string> allowed)
    {
        if (allowed is null || allowed.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(remoteIp)) return false;
        if (!IPAddress.TryParse(remoteIp, out var ip)) return false;

        foreach (var entry in allowed)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (Matches(ip, entry.Trim())) return true;
        }
        return false;
    }

    public bool TryAcquire(string? remoteIp, int rateLimitPerMinute)
    {
        if (rateLimitPerMinute <= 0) return true;
        var key = string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp.Trim();
        var now = DateTime.UtcNow;
        CleanupIfNeeded(now);

        var counter = _windows.GetOrAdd(key, _ => new WindowCounter());
        lock (counter.Sync)
        {
            if ((now - counter.WindowStart).TotalMinutes >= 1)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }
            if (counter.Count >= rateLimitPerMinute) return false;
            counter.Count++;
            return true;
        }
    }

    private void CleanupIfNeeded(DateTime now)
    {
        if ((now - _lastCleanupUtc).TotalMinutes < 5) return;
        lock (_cleanupLock)
        {
            if ((now - _lastCleanupUtc).TotalMinutes < 5) return;
            foreach (var kv in _windows)
            {
                lock (kv.Value.Sync)
                {
                    if ((now - kv.Value.WindowStart).TotalMinutes > 2)
                        _windows.TryRemove(kv.Key, out _);
                }
            }
            _lastCleanupUtc = now;
        }
    }

    private static bool Matches(IPAddress ip, string rule)
    {
        if (rule.Contains('/', StringComparison.Ordinal))
            return MatchCidr(ip, rule);
        return IPAddress.TryParse(rule, out var exact) && ip.Equals(exact);
    }

    private static bool MatchCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix))
            return false;
        if (ip.AddressFamily != network.AddressFamily) return false;

        var ipBytes = ip.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (ipBytes.Length != netBytes.Length) return false;
        var maxBits = ipBytes.Length * 8;
        if (prefix < 0 || prefix > maxBits) return false;

        var fullBytes = prefix / 8;
        var remainBits = prefix % 8;
        for (var i = 0; i < fullBytes; i++)
            if (ipBytes[i] != netBytes[i]) return false;
        if (remainBits == 0) return true;
        var mask = (byte)(0xFF << (8 - remainBits));
        return (ipBytes[fullBytes] & mask) == (netBytes[fullBytes] & mask);
    }

    private sealed class WindowCounter
    {
        public object Sync { get; } = new();
        public DateTime WindowStart { get; set; } = DateTime.UtcNow;
        public int Count { get; set; }
    }
}
