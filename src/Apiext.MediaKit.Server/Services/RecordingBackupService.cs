using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>录像备份编排：把本地录像目录增量同步到 SMB/NFS/S3 挂载目录。</summary>
public class RecordingBackupService
{
    private readonly AppDbContext _db;
    private readonly string _defaultSource;

    public RecordingBackupService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _defaultSource = config["Recording:SourcePath"] ?? string.Empty;
    }

    public string DefaultSourcePath => _defaultSource;

    public Task<List<BackupTarget>> ListAsync()
        => _db.BackupTargets.OrderBy(t => t.CreatedAt).ToListAsync();

    public async Task<(bool Ok, string? Error)> AddAsync(BackupTarget target)
    {
        var (valid, error) = Validate(target);
        if (!valid) return (false, error);

        if (await _db.BackupTargets.AnyAsync(t => t.Name == target.Name))
            return (false, "该名称已存在");

        target.CreatedAt = DateTime.UtcNow;
        _db.BackupTargets.Add(target);
        await _db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> UpdateAsync(BackupTarget target)
    {
        var (valid, error) = Validate(target);
        if (!valid) return (false, error);

        var existing = await _db.BackupTargets.FindAsync(target.Id);
        if (existing == null) return (false, "记录不存在");

        if (await _db.BackupTargets.AnyAsync(t => t.Id != target.Id && t.Name == target.Name))
            return (false, "该名称已存在");

        existing.Name = target.Name;
        existing.Type = target.Type;
        existing.SourcePath = target.SourcePath;
        existing.TargetPath = target.TargetPath;
        existing.Endpoint = target.Endpoint;
        existing.Region = target.Region;
        existing.AccessKey = target.AccessKey;
        existing.SecretKey = target.SecretKey;
        existing.Bucket = target.Bucket;
        existing.Prefix = target.Prefix;
        existing.Enabled = target.Enabled;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    private static (bool Ok, string? Error) Validate(BackupTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Name))
            return (false, "名称不能为空");

        if (target.Type == "s3")
        {
            if (string.IsNullOrWhiteSpace(target.Endpoint) ||
                string.IsNullOrWhiteSpace(target.AccessKey) ||
                string.IsNullOrWhiteSpace(target.SecretKey) ||
                string.IsNullOrWhiteSpace(target.Bucket))
            {
                return (false, "S3 类型需填写端点、AccessKey、SecretKey、Bucket");
            }
        }
        else if (string.IsNullOrWhiteSpace(target.TargetPath))
        {
            return (false, "挂载目录不能为空");
        }

        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteAsync(int id)
    {
        var existing = await _db.BackupTargets.FindAsync(id);
        if (existing == null) return (false, "记录不存在");

        _db.BackupTargets.Remove(existing);
        await _db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> ToggleAsync(int id, bool enabled)
    {
        var existing = await _db.BackupTargets.FindAsync(id);
        if (existing == null) return (false, "记录不存在");

        existing.Enabled = enabled;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>同步所有已启用的备份目标（供后台定时任务与手动「全部备份」使用）。</summary>
    public async Task SyncAllAsync()
    {
        var targets = await _db.BackupTargets.Where(t => t.Enabled).ToListAsync();
        foreach (var target in targets)
        {
            await SyncOneAsync(target);
        }
    }

    /// <summary>增量同步单个目标，返回 (复制文件数, 字节数, 结果描述)。</summary>
    public Task<(int Copied, long Bytes, string Message)> SyncAsync(BackupTarget target)
        => SyncOneAsync(target);

    private async Task<(int Copied, long Bytes, string Message)> SyncOneAsync(BackupTarget target)
    {
        var source = string.IsNullOrWhiteSpace(target.SourcePath) ? _defaultSource : target.SourcePath;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            return await FailAsync(target, $"源目录不存在：{source}");
        }

        if (target.Type == "s3")
        {
            return await SyncS3Async(target, source);
        }

        if (string.IsNullOrWhiteSpace(target.TargetPath))
        {
            return await FailAsync(target, "挂载目录未配置");
        }

        try
        {
            Directory.CreateDirectory(target.TargetPath);

            int copied = 0;
            long bytes = 0;
            // 跳过最近 2 分钟内仍在写入的分片，避免复制到不完整的 MP4
            var cutoff = DateTime.UtcNow.AddMinutes(-2);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc > cutoff) continue;

                var rel = Path.GetRelativePath(source, file);
                var dest = Path.Combine(target.TargetPath, rel);
                var destInfo = new FileInfo(dest);
                if (destInfo.Exists && destInfo.Length == fi.Length && destInfo.LastWriteTimeUtc >= fi.LastWriteTimeUtc)
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
                copied++;
                bytes += fi.Length;
            }

            target.LastSyncAt = DateTime.UtcNow;
            target.LastResult = $"已同步 {copied} 个文件（{FormatBytes(bytes)}）";
            await _db.SaveChangesAsync();
            return (copied, bytes, target.LastResult);
        }
        catch (Exception ex)
        {
            return await FailAsync(target, $"备份失败：{ex.Message}");
        }
    }

    /// <summary>S3 原生直传：把录像文件 PUT 到 S3 兼容存储（增量，按对象大小判断）。</summary>
    private async Task<(int Copied, long Bytes, string Message)> SyncS3Async(BackupTarget target, string source)
    {
        if (string.IsNullOrWhiteSpace(target.Endpoint) ||
            string.IsNullOrWhiteSpace(target.AccessKey) ||
            string.IsNullOrWhiteSpace(target.SecretKey) ||
            string.IsNullOrWhiteSpace(target.Bucket))
        {
            return await FailAsync(target, "S3 配置不完整（端点/AccessKey/SecretKey/Bucket）");
        }

        try
        {
            using var s3 = new S3StorageClient(target.Endpoint, target.Region, target.AccessKey, target.SecretKey, target.Bucket);

            int copied = 0;
            long bytes = 0;
            var cutoff = DateTime.UtcNow.AddMinutes(-2);
            var prefix = string.IsNullOrWhiteSpace(target.Prefix) ? "" : target.Prefix.Trim('/');

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc > cutoff) continue;

                var rel = Path.GetRelativePath(source, file).Replace('\\', '/');
                var key = prefix.Length == 0 ? rel : $"{prefix}/{rel}";

                var existing = await s3.HeadObjectAsync(key);
                if (existing == fi.Length) continue;

                if (await s3.PutObjectAsync(key, file))
                {
                    copied++;
                    bytes += fi.Length;
                }
            }

            target.LastSyncAt = DateTime.UtcNow;
            target.LastResult = $"已上传 {copied} 个对象（{FormatBytes(bytes)}）";
            await _db.SaveChangesAsync();
            return (copied, bytes, target.LastResult);
        }
        catch (Exception ex)
        {
            return await FailAsync(target, $"S3 备份失败：{ex.Message}");
        }
    }

    private async Task<(int, long, string)> FailAsync(BackupTarget target, string message)
    {
        target.LastResult = message;
        await _db.SaveChangesAsync();
        return (0, 0, message);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:F2} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
