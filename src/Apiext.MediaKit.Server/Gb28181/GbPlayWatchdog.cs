using System.Collections.Concurrent;
using Apiext.MediaKit.Server.Apis;
using Apiext.MediaKit.Server.Services;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>
/// 无人观看自动断流：周期检查 ZLM 读者数，对无观众超过阈值（IdleTimeout）的国标点播流自动停止，
/// 释放 RTP 端口与设备推流。级联转发流（cascade_ 前缀）不参与（由上级平台持有）。
/// </summary>
public class GbPlayWatchdog : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GbPlayWatchdog> _logger;
    private readonly IConfiguration _config;

    private readonly ConcurrentDictionary<string, DateTime> _idleSince = new(StringComparer.OrdinalIgnoreCase);

    public GbPlayWatchdog(IServiceScopeFactory scopeFactory, ILogger<GbPlayWatchdog> logger, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var section = _config.GetSection("Gb28181");
        var interval = section.GetValue("WatchdogInterval", 30);
        var idleTimeout = section.GetValue("IdleTimeout", 60);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(idleTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GB28181] 无人观看检查异常");
            }
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
        }
    }

    private async Task CheckAsync(int idleTimeout)
    {
        using var scope = _scopeFactory.CreateScope();
        var zlm = scope.ServiceProvider.GetRequiredService<ZlmService>();
        var gb = scope.ServiceProvider.GetRequiredService<Gb28181Service>();

        var media = await zlm.GetMediaListAsync("__defaultVhost__", "rtp", null);
        if (media.Code != 0) return;

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media.Data ?? new List<MediaItem>())
        {
            var stream = item.Stream;
            if (string.IsNullOrEmpty(stream) || stream.StartsWith("cascade_", StringComparison.OrdinalIgnoreCase))
                continue;

            active.Add(stream);

            if (item.ReaderCount <= 0)
            {
                var now = DateTime.Now;
                if (_idleSince.TryGetValue(stream, out var since))
                {
                    if ((now - since).TotalSeconds >= idleTimeout)
                    {
                        _logger.LogInformation("[GB28181] 无人观看超时，自动停止点播：{Stream}", stream);
                        _idleSince.TryRemove(stream, out _);
                        await gb.StopPlayAsync(stream);
                    }
                }
                else
                {
                    _idleSince[stream] = now;
                }
            }
            else
            {
                _idleSince.TryRemove(stream, out _);
            }
        }

        // 清理已不存在的流
        foreach (var key in _idleSince.Keys.Where(k => !active.Contains(k)).ToList())
        {
            _idleSince.TryRemove(key, out _);
        }
    }
}
