namespace Apiext.MediaKit.Server.Services;

/// <summary>录像备份后台任务：按周期同步所有已启用的备份目标。</summary>
public class RecordingBackupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RecordingBackupHostedService> _logger;

    public RecordingBackupHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<RecordingBackupHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _config.GetValue("Recording:BackupIntervalMinutes", 15);
        if (minutes <= 0) minutes = 15;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        _logger.LogInformation("[Backup] 录像备份任务已启动，周期 {Minutes} 分钟", minutes);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SyncAllAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private async Task SyncAllAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<RecordingBackupService>();
            await service.SyncAllAsync();
            _logger.LogDebug("[Backup] 本轮备份同步完成 ({Elapsed}ms)", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Backup] 录像备份任务执行失败 ({Elapsed}ms)", sw.ElapsedMilliseconds);
        }
    }
}
