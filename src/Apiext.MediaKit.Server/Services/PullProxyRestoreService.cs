using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 启动恢复：容器/进程重启后 ZLM 内存里的拉流代理会全部丢失（DB 记录还在，
/// 表现为「列表里明明有设备但流全部离线」）。本服务在 ZLM 就绪后，
/// 把「非按需」以及「按需但开录像」的流重新拉起来，免去人工逐个点启动。
/// 纯按需（无人观看即断流）的流不在此列，仍由播放请求触发。
/// </summary>
public class PullProxyRestoreService : BackgroundService
{
    private const int MaxWaitTries = 30;          // 30 × 3s = 90s
    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PullInterval = TimeSpan.FromMilliseconds(500);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PullProxyRestoreService> _logger;

    public PullProxyRestoreService(IServiceScopeFactory scopeFactory, ILogger<PullProxyRestoreService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await WaitZlmReadyAsync(stoppingToken))
        {
            _logger.LogWarning("[PullProxy] 启动恢复已跳过：ZLM {Sec}s 内未就绪", MaxWaitTries * 3);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pull = scope.ServiceProvider.GetRequiredService<PullProxyService>();

            // 与 AddAsync 的判定保持一致：EnableOnDemand && !EnableRecord 才不立即拉流
            var targets = await db.PullProxies.AsNoTracking()
                .Where(p => !p.EnableOnDemand || p.EnableRecord)
                .OrderBy(p => p.Id)
                .ToListAsync(stoppingToken);

            if (targets.Count == 0) return;

            _logger.LogInformation("[PullProxy] 启动恢复：{Count} 路需要常驻拉流", targets.Count);

            var restored = 0;
            foreach (var p in targets)
            {
                if (stoppingToken.IsCancellationRequested) return;

                // EnsureStreamAsync 内部已判断是否在线，失败会记 Warning，不抛出
                await pull.EnsureStreamAsync(p.App, p.Stream);
                restored++;

                // 逐个拉流，避免同时冲击源端与 ZLM
                try { await Task.Delay(PullInterval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }

            _logger.LogInformation("[PullProxy] 启动恢复完成：已处理 {Restored}/{Count} 路", restored, targets.Count);
        }
        catch (OperationCanceledException)
        {
            // 应用正在停止
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PullProxy] 启动恢复异常");
        }
    }

    /// <summary>等待 ZLM 可用（同容器部署时两个进程几乎同时启动，ZLM 往往慢几秒）。</summary>
    private async Task<bool> WaitZlmReadyAsync(CancellationToken stoppingToken)
    {
        for (var i = 1; i <= MaxWaitTries; i++)
        {
            if (stoppingToken.IsCancellationRequested) return false;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var zlm = scope.ServiceProvider.GetRequiredService<ZlmService>();
                var resp = await zlm.GetMediaListAsync();
                if (resp.Code == 0) return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PullProxy] 启动恢复：等待 ZLM 就绪 {Try}/{Max}", i, MaxWaitTries);
            }

            try { await Task.Delay(WaitInterval, stoppingToken); }
            catch (OperationCanceledException) { return false; }
        }

        return false;
    }
}
