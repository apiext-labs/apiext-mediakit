using System.Collections.Concurrent;

namespace Apiext.MediaKit.Server.Services;

/// <summary>在线流缓存条目（由 ZLM on_stream_changed hook 维护）。</summary>
public sealed class OnlineStream
{
    public string Vhost { get; init; } = "__defaultVhost__";
    public string App { get; init; } = string.Empty;
    public string Stream { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public DateTime RegisteredAtUtc { get; init; }
}

/// <summary>
/// 在线流缓存：on_stream_changed（注册/注销）实时更新，供设备列表等展示在线状态时读取，
/// 减少对 ZLM getMediaList 的依赖。注册表按 vhost/app/stream 去重。
/// </summary>
public sealed class ZlmOnlineCache
{
    private readonly ConcurrentDictionary<string, OnlineStream> _streams = new(StringComparer.OrdinalIgnoreCase);

    public static string KeyOf(string vhost, string app, string stream) => $"{vhost}/{app}/{stream}";

    public void MarkOnline(string? vhost, string? app, string? stream, string? schema)
    {
        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(stream)) return;
        var key = KeyOf(vhost ?? "__defaultVhost__", app, stream);
        _streams[key] = new OnlineStream
        {
            Vhost = vhost ?? "__defaultVhost__",
            App = app,
            Stream = stream,
            Schema = schema ?? string.Empty,
            RegisteredAtUtc = DateTime.UtcNow,
        };
    }

    public void MarkOffline(string? vhost, string? app, string? stream)
    {
        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(stream)) return;
        _streams.TryRemove(KeyOf(vhost ?? "__defaultVhost__", app, stream), out _);
    }

    public bool TryGet(string? vhost, string? app, string? stream, out OnlineStream entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(stream)) return false;
        return _streams.TryGetValue(KeyOf(vhost ?? "__defaultVhost__", app, stream), out entry!);
    }

    /// <summary>清空缓存（ZLM 重启时调用，避免残留流被长期误判在线）。</summary>
    public void Clear() => _streams.Clear();

    public int Count => _streams.Count;
}
