using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Persistence;

/// <summary>PBKDF2 密码散列，无第三方依赖。</summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;

    public static (string Hash, string Salt) Hash(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToBase64String(saltBytes);
        return (Hash(password, salt), salt);
    }

    public static string Hash(string password, string salt)
    {
        var saltBytes = Convert.FromBase64String(salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string salt, string expectedHash)
    {
        var actual = Convert.FromBase64String(Hash(password, salt));
        var expected = Convert.FromBase64String(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public static class DbInitializer
{
    public static void Initialize(AppDbContext db, IConfiguration config)
    {
        db.Database.EnsureCreated();

        // EnsureCreated 对已存在的数据库不会补建新增表，这里兜底补建 GB28181 相关表
        EnsureGbTables(db);

        if (db.AdminUsers.Any())
        {
            return;
        }

        var username = config["Admin:Username"] ?? "admin";
        var password = config["Admin:Password"] ?? "admin123";
        var (hash, salt) = PasswordHasher.Hash(password);

        db.AdminUsers.Add(new AdminUser
        {
            Username = username,
            PasswordHash = hash,
            Salt = salt,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void EnsureGbTables(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS GbDevices (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "DeviceId TEXT NOT NULL, " +
            "Name TEXT NOT NULL, " +
            "Manufacturer TEXT NOT NULL, " +
            "Model TEXT NOT NULL, " +
            "Host TEXT NOT NULL, " +
            "Port INTEGER NOT NULL, " +
            "Online INTEGER NOT NULL, " +
            "RegisteredAt TEXT NOT NULL, " +
            "LastKeepalive TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL);");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS GbChannels (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "ChannelId TEXT NOT NULL, " +
            "DeviceId TEXT NOT NULL, " +
            "Name TEXT NOT NULL, " +
            "Manufacturer TEXT NOT NULL, " +
            "Model TEXT NOT NULL, " +
            "Owner TEXT NOT NULL, " +
            "CivilCode TEXT NOT NULL, " +
            "Address TEXT NOT NULL, " +
            "Parental INTEGER NOT NULL, " +
            "ParentId TEXT NOT NULL, " +
            "Status TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL);");

        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_GbDevices_DeviceId ON GbDevices (DeviceId);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_GbChannels_ChannelId ON GbChannels (ChannelId);");
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_GbChannels_DeviceId ON GbChannels (DeviceId);");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS GbAlarms (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "DeviceId TEXT NOT NULL, " +
            "AlarmPriority TEXT NOT NULL, " +
            "AlarmMethod TEXT NOT NULL, " +
            "AlarmTime TEXT NOT NULL, " +
            "AlarmDescription TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_GbAlarms_DeviceId ON GbAlarms (DeviceId);");
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_GbAlarms_AlarmTime ON GbAlarms (AlarmTime);");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS CascadeUppers (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Name TEXT NOT NULL, " +
            "Ip TEXT NOT NULL, " +
            "Port INTEGER NOT NULL, " +
            "PlatformId TEXT NOT NULL, " +
            "Domain TEXT NOT NULL, " +
            "Realm TEXT NOT NULL, " +
            "Password TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_CascadeUppers_Ip_Port ON CascadeUppers (Ip, Port);");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS AppSettings (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Key TEXT NOT NULL, " +
            "Value TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_AppSettings_Key ON AppSettings (Key);");

        // PullProxy 新增列（对已存在数据库补列）
        TryAddColumn(db, "PullProxies", "EnableOnDemand", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(db, "PullProxies", "EnableRecord", "INTEGER NOT NULL DEFAULT 0");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS OnvifDevices (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Name TEXT NOT NULL, " +
            "Host TEXT NOT NULL, " +
            "Port INTEGER NOT NULL, " +
            "Username TEXT NOT NULL, " +
            "Password TEXT NOT NULL, " +
            "Manufacturer TEXT NOT NULL, " +
            "Model TEXT NOT NULL, " +
            "RtspUrl TEXT NULL, " +
            "ProfileToken TEXT NULL, " +
            "CreatedAt TEXT NOT NULL);");

        // OnvifDevice 新增列（PTZ 方向自适应缓存，对已存在数据库补列）
        TryAddColumn(db, "OnvifDevices", "PtzAxesResolved", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(db, "OnvifDevices", "PtzPanReversed", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(db, "OnvifDevices", "PtzTiltReversed", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(db, "OnvifDevices", "PtzZoomReversed", "INTEGER NOT NULL DEFAULT 0");

        // PullProxy 新增列（取流模式，对已存在数据库补列）
        TryAddColumn(db, "PullProxies", "PullMode", "TEXT NOT NULL DEFAULT 'native'");
        TryAddColumn(db, "PullProxies", "FfmpegKey", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(db, "PullProxies", "OutputCodec", "TEXT NOT NULL DEFAULT 'copy'");

        // 录像备份目标表
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS BackupTargets (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Name TEXT NOT NULL, " +
            "Type TEXT NOT NULL, " +
            "SourcePath TEXT NOT NULL, " +
            "TargetPath TEXT NOT NULL, " +
            "Enabled INTEGER NOT NULL, " +
            "LastSyncAt TEXT NULL, " +
            "LastResult TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_BackupTargets_Name ON BackupTargets (Name);");

        // BackupTargets 新增 S3 直传列（对已存在数据库补列）
        TryAddColumn(db, "BackupTargets", "Endpoint", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(db, "BackupTargets", "Region", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(db, "BackupTargets", "AccessKey", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(db, "BackupTargets", "SecretKey", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(db, "BackupTargets", "Bucket", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(db, "BackupTargets", "Prefix", "TEXT NOT NULL DEFAULT ''");

        // GB28181 预登记设备表（白名单鉴权模式）
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS GbPresetDevices (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "DeviceId TEXT NOT NULL, " +
            "Name TEXT NOT NULL, " +
            "Password TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_GbPresetDevices_DeviceId ON GbPresetDevices (DeviceId);");

        // 全局访问令牌表（保护播放地址与云台控制）
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS ApiTokens (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Name TEXT NOT NULL, " +
            "Token TEXT NOT NULL, " +
            "ExpiresAt TEXT NULL, " +
            "Enabled INTEGER NOT NULL, " +
            "LastUsedAt TEXT NULL, " +
            "CreatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ApiTokens_Token ON ApiTokens (Token);");

        // 设备自定义分组（播放页设备树分组）与成员关联
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS DeviceGroups (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Name TEXT NOT NULL, " +
            "SortOrder INTEGER NOT NULL, " +
            "CreatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_DeviceGroups_Name ON DeviceGroups (Name);");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS DeviceGroupMembers (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Kind TEXT NOT NULL, " +
            "App TEXT NOT NULL, " +
            "Stream TEXT NOT NULL, " +
            "GroupId INTEGER NOT NULL, " +
            "CreatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_DeviceGroupMembers_Kind_App_Stream ON DeviceGroupMembers (Kind, App, Stream);");
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_DeviceGroupMembers_GroupId ON DeviceGroupMembers (GroupId);");

        // 本地录像索引（ZLM on_record_mp4 hook 写入，对账服务补齐/校正）
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS RecordingFiles (" +
            "Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Vhost TEXT NOT NULL, " +
            "App TEXT NOT NULL, " +
            "Stream TEXT NOT NULL, " +
            "FileName TEXT NOT NULL, " +
            "Folder TEXT NOT NULL, " +
            "FilePath TEXT NOT NULL, " +
            "Url TEXT NOT NULL, " +
            "FileSize INTEGER NOT NULL, " +
            "DurationSec INTEGER NOT NULL, " +
            "StartTimeUtc TEXT NOT NULL, " +
            "Source TEXT NOT NULL DEFAULT 'hook', " +
            "LastSeenAtUtc TEXT NULL, " +
            "MissingAtUtc TEXT NULL, " +
            "ReconcileMissCount INTEGER NOT NULL DEFAULT 0, " +
            "CreatedAt TEXT NOT NULL);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_RecordingFiles_App_Stream_FileName ON RecordingFiles (App, Stream, FileName);");
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_RecordingFiles_StartTimeUtc ON RecordingFiles (StartTimeUtc);");

        // 对账状态字段（幂等补列，兼容已存在的表）
        TryAddColumn(db, "RecordingFiles", "Source", "TEXT NOT NULL DEFAULT 'hook'");
        TryAddColumn(db, "RecordingFiles", "LastSeenAtUtc", "TEXT NULL");
        TryAddColumn(db, "RecordingFiles", "MissingAtUtc", "TEXT NULL");
        TryAddColumn(db, "RecordingFiles", "ReconcileMissCount", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void TryAddColumn(AppDbContext db, string table, string column, string definition)
    {
        // 先查列是否已存在，避免重复 ALTER 抛错污染启动日志
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
            opened = true;
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
            }
        }
        finally
        {
            if (opened) conn.Close();
        }
    }

}
