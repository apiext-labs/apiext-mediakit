using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 按需拉流：定期检查启用按需拉流的流，无人观看超时自动断流。
/// 当 ZlmHook 的 on_stream_none_reader 接管（ReplaceOnDemandPolling=true）时，本服务停止轮询。
/// </summary>
public class OnDemandPullService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OnDemandPullService> _logger;
    private readonly IOptions<ZlmHookOptions> _hookOptions;

    // 流 key -> 最后一次有观众的时间
    private readonly Dictionary<string, DateTime> _lastReaderTime = new(StringComparer.OrdinalIgnoreCase);

    public OnDemandPullService(
        IServiceScopeFactory scopeFactory,
        ILogger<OnDemandPullService> logger,
        IOptions<ZlmHookOptions> hookOptions)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _hookOptions = hookOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hook = _hookOptions.Value;
        if (hook.Enabled && hook.ReplaceOnDemandPolling)
        {
            _logger.LogInformation("[OnDemand] 已由 ZLM on_stream_none_reader hook 接管，轮询不再启动");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OnDemand] 检查失败");
            }
        }
    }

    private async Task CheckAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var zlm = scope.ServiceProvider.GetRequiredService<ZlmService>();
        var pullProxies = scope.ServiceProvider.GetRequiredService<PullProxyService>();

        // 启用按需拉流且未录像的流（录像中的流不断流）
        var proxies = await db.PullProxies
            .Where(p => p.EnableOnDemand && !p.EnableRecord)
            .ToListAsync();

        var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readerCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var mediaResp = await zlm.GetMediaListAsync();
            if (mediaResp.Code == 0 && mediaResp.Data != null)
            {
                foreach (var m in mediaResp.Data)
                {
                    if (m.App == null || m.Stream == null) continue;
                    var key = $"{m.App}/{m.Stream}";
                    online.Add(key);
                    readerCount[key] = m.TotalReaderCount;
                }
            }
        }
        catch
        {
            return; // ZLM 不可达，本轮跳过
        }

        var now = DateTime.UtcNow;
        foreach (var p in proxies)
        {
            var key = $"{p.App}/{p.Stream}";
            if (!online.Contains(key))
            {
                _lastReaderTime.Remove(key);
                continue;
            }

            if (readerCount.GetValueOrDefault(key) > 0)
            {
                _lastReaderTime[key] = now;
            }
            else if (_lastReaderTime.TryGetValue(key, out var last))
            {
                if ((now - last).TotalSeconds >= 60)
                {
                    try
                    {
                        // 断流复用拉流代理服务（按取流模式走 delFFmpegSource / delStreamProxy）
                        await pullProxies.StopStreamAsync(p);
                        _lastReaderTime.Remove(key);
                        _logger.LogInformation("[OnDemand] 流 {App}/{Stream} 无人观看超时，已断流", p.App, p.Stream);
                    }
                    catch
                    {
                        // 忽略
                    }
                }
            }
            else
            {
                // 首次发现上线但尚无观众：记录上线时间，之后仍无人观看则超时断流
                _lastReaderTime[key] = now;
            }
        }
    }
}
