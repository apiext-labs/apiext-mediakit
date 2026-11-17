using System.Globalization;
using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 录像索引对账：hook 是主链路（切片完成即入库），本服务负责校正两侧不一致。
/// 1) 索引有、文件没了 → 累计未命中次数，达到阈值后标记 MissingAtUtc（不删记录，便于回溯）；
/// 2) 文件在、索引没有（历史数据 / hook 丢失）→ 按目录结构补录（Source=reconcile）。
/// 默认只对账最近 7 天，避免每次全盘扫描。
/// </summary>
public sealed class RecordingReconcileService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RecordingReconcileService> _logger;

    public RecordingReconcileService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<RecordingReconcileService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_config.GetValue("Recording:ReconcileEnabled", true))
        {
            _logger.LogInformation("[Reconcile] 未启用（Recording:ReconcileEnabled=false）");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, _config.GetValue("Recording:ReconcileIntervalMinutes", 30)));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await ReconcileOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Reconcile] 本轮失败");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>执行一轮对账（后台循环与手动触发共用）。</summary>
    public async Task ReconcileOnceAsync(CancellationToken ct = default)
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root))
        {
            _logger.LogDebug("[Reconcile] 录制目录不存在，跳过：{Root}", root);
            return;
        }

        var days = Math.Clamp(_config.GetValue("Recording:ReconcileDays", 7), 1, 90);
        var missThreshold = Math.Clamp(_config.GetValue("Recording:ReconcileMissThreshold", 3), 1, 20);
        var folderPrefix = _config["Recording:ZlFolderPrefix"] ?? "record";
        var since = DateTime.UtcNow.AddDays(-days);
        var now = DateTime.UtcNow;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.RecordingFiles
            .Where(r => r.StartTimeUtc >= since)
            .ToListAsync(ct);

        // ---------- 1) 索引 -> 文件 ----------
        var markedMissing = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var path = ResolveLocalPath(root, row.Folder, row.FileName, folderPrefix);
            if (path is not null && File.Exists(path))
            {
                row.LastSeenAtUtc = now;
                row.ReconcileMissCount = 0;
                if (row.MissingAtUtc is not null)
                {
                    row.MissingAtUtc = null;
                    _logger.LogInformation("[Reconcile] 文件恢复 {App}/{Stream} {File}", row.App, row.Stream, row.FileName);
                }

                if (row.FileSize <= 0)
                {
                    row.FileSize = new FileInfo(path).Length;
                }
                continue;
            }

            row.ReconcileMissCount++;
            if (row.ReconcileMissCount >= missThreshold && row.MissingAtUtc is null)
            {
                row.MissingAtUtc = now;
                markedMissing++;
                _logger.LogWarning("[Reconcile] 文件丢失 {App}/{Stream} {File}（连续 {Count} 次未命中）",
                    row.App, row.Stream, row.FileName, row.ReconcileMissCount);
            }
        }

        // ---------- 2) 文件 -> 索引 ----------
        var known = new HashSet<string>(
            rows.Select(r => Key(r.App, r.Stream, r.FileName)),
            StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var appDir in SafeEnumerateDirectories(root))
        {
            var app = Path.GetFileName(appDir);
            foreach (var streamDir in SafeEnumerateDirectories(appDir))
            {
                var stream = Path.GetFileName(streamDir);
                for (var d = 0; d < days; d++)
                {
                    var day = DateTime.Now.Date.AddDays(-d);
                    var dateDir = Path.Combine(streamDir, day.ToString("yyyy-MM-dd"));
                    if (!Directory.Exists(dateDir)) continue;

                    foreach (var file in SafeEnumerateFiles(dateDir, "*.mp4"))
                    {
                        var fileName = Path.GetFileName(file);
                        if (!known.Add(Key(app, stream, fileName))) continue;

                        var info = new FileInfo(file);
                        db.RecordingFiles.Add(new RecordingFile
                        {
                            Vhost = "__defaultVhost__",
                            App = app,
                            Stream = stream,
                            FileName = fileName,
                            Folder = $"{folderPrefix}/{app}/{stream}/{day:yyyy-MM-dd}",
                            FilePath = file,
                            Url = string.Empty,
                            FileSize = info.Length,
                            DurationSec = 0,
                            StartTimeUtc = ParseStartTime(fileName, day),
                            Source = RecordingFile.SourceReconcile,
                            LastSeenAtUtc = now,
                            CreatedAt = now,
                        });
                        added++;
                    }
                }
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "[Reconcile] 写入索引失败（可能存在并发补录）");
            }
        }

        if (added > 0 || markedMissing > 0)
        {
            _logger.LogInformation("[Reconcile] 完成：补录 {Added} 条，标记丢失 {Missing} 条", added, markedMissing);
        }
    }

    private string ResolveRoot()
    {
        var root = _config["Recording:SourcePath"];
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(AppContext.BaseDirectory, "record")
            : root;
    }

    /// <summary>
    /// DB 里的 Folder 形如 record/{app}/{stream}/{date}（首段是 ZLM 的 [record] appName），
    /// 本地根目录已经指向该 appName 目录，故去掉首段后拼接。
    /// </summary>
    private static string? ResolveLocalPath(string root, string folder, string fileName, string folderPrefix)
    {
        if (string.IsNullOrEmpty(fileName)) return null;

        var relative = folder;
        if (!string.IsNullOrEmpty(folderPrefix) &&
            relative.StartsWith(folderPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[(folderPrefix.Length + 1)..];
        }

        relative = relative.Trim('/');
        return relative.Length == 0
            ? Path.Combine(root, fileName)
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar), fileName);
    }

    /// <summary>从文件名解析切片开始时间：支持 "2026-11-12-12-00-00.mp4" 与 "12-00-00.mp4"。</summary>
    private static DateTime ParseStartTime(string fileName, DateTime day)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        if (DateTime.TryParseExact(name, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var full))
        {
            return full.ToUniversalTime();
        }

        if (DateTime.TryParseExact(name, "HH-mm-ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var timeOnly))
        {
            return day.Date.Add(timeOnly.TimeOfDay).ToUniversalTime();
        }

        return day.ToUniversalTime();
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string Key(string app, string stream, string fileName) => $"{app}/{stream}/{fileName}";
}
