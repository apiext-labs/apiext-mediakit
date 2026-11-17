using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 录像保留期清理：按 Recording:RetentionDays 删除过期录像文件，避免本地磁盘无限增长。
/// 默认 0 = 不清理（需显式开启）。启动后延迟 5 分钟执行首轮，之后每 6 小时一轮。
/// </summary>
public sealed class RecordingRetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RecordingRetentionService> _logger;

    public RecordingRetentionService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<RecordingRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanupOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Retention] 执行失败");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>执行一轮清理（供后台循环与手动触发复用）。</summary>
    public void CleanupOnce()
    {
        var days = _config.GetValue("Recording:RetentionDays", 0);
        if (days <= 0)
        {
            return;
        }

        var root = _config["Recording:SourcePath"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "record");
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var deleted = 0;
        long freedBytes = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                freedBytes += info.Length;
                info.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Retention] 删除失败 {File}", file);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("[Retention] 已删除 {Count} 个超过 {Days} 天的录像，释放 {Mb:F1} MB",
                deleted, days, freedBytes / 1024.0 / 1024.0);
        }

        // 同步清理过期的录像索引（按 StartTimeUtc 判断，不依赖文件路径映射）
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var removed = db.RecordingFiles.Where(r => r.StartTimeUtc < cutoff).ExecuteDelete();
            if (removed > 0)
            {
                _logger.LogInformation("[Retention] 已删除 {Count} 条过期录像索引", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Retention] 清理录像索引失败");
        }
    }
}
