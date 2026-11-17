using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 录像计划（定时录制）：按 Recording:Schedule（如 "08:00-20:00"，支持跨天 "20:00-08:00"；
/// 留空或 "always" = 不干预，按各流自身录像开关）每分钟检查，
/// 在进入/离开时间窗口时批量对 EnableRecord=true 的拉流代理启停录像。
/// </summary>
public sealed class RecordingScheduleService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RecordingScheduleService> _logger;
    private bool? _lastInWindow;

    public RecordingScheduleService(
        IConfiguration config, IServiceScopeFactory scopeFactory, ILogger<RecordingScheduleService> logger)
    {
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RecordSchedule] 检查失败");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync()
    {
        var schedule = _config["Recording:Schedule"];
        if (string.IsNullOrWhiteSpace(schedule) ||
            schedule.Equals("always", StringComparison.OrdinalIgnoreCase))
        {
            return; // 未配置 = 不干预（按各流自身录像开关）
        }

        var inWindow = IsInWindow(schedule, DateTime.Now);
        if (_lastInWindow == inWindow)
        {
            return; // 状态未变化，避免每分钟重复调用 ZLM
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var zlm = scope.ServiceProvider.GetRequiredService<ZlmService>();
        var streams = await db.PullProxies.Where(p => p.EnableRecord).ToListAsync();

        foreach (var p in streams)
        {
            try
            {
                if (inWindow)
                {
                    await zlm.StartRecordAsync(p.App, p.Stream);
                }
                else
                {
                    await zlm.StopRecordAsync(p.App, p.Stream);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[RecordSchedule] {Action} 失败 {App}/{Stream}",
                    inWindow ? "开始" : "停止", p.App, p.Stream);
            }
        }

        _lastInWindow = inWindow;
        _logger.LogInformation("[RecordSchedule] {Action}录像：{Count} 路（窗口 {Schedule}）",
            inWindow ? "开始" : "停止", streams.Count, schedule);
    }

    /// <summary>判断当前时间是否落在 "HH:mm-HH:mm" 窗口内（支持跨天）；格式非法时按全天处理。</summary>
    public static bool IsInWindow(string schedule, DateTime now)
    {
        var parts = schedule.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return true;
        }

        if (!TimeSpan.TryParse(parts[0], out var start) || !TimeSpan.TryParse(parts[1], out var end))
        {
            return true;
        }

        var t = now.TimeOfDay;
        return start <= end
            ? t >= start && t < end
            : t >= start || t < end; // 跨天
    }
}
