using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.EntityFrameworkCore;

public static class MediaKitSchemaBootstrapper
{
    public static async Task UpgradeAsync(MediaKitDbContext db, CancellationToken cancellationToken = default)
    {
        // 项目尚处于首版阶段，先用幂等 SQL 兼容 EnsureCreated 生成的旧库；正式发布后改由 EF Migration 接管。
        var proxyColumns = await GetColumnsAsync(db, "MediaKitPullProxies", cancellationToken);
        await AddColumnAsync(db, proxyColumns, "SourceUrlsJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnAsync(db, proxyColumns, "Remark", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnAsync(db, proxyColumns, "ProtocolPresetId", "TEXT NULL", cancellationToken);
        await AddColumnAsync(db, proxyColumns, "OnDemand", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnAsync(db, proxyColumns, "RetryCount", "INTEGER NOT NULL DEFAULT -1", cancellationToken);
        await AddColumnAsync(db, proxyColumns, "TimeoutSeconds", "INTEGER NOT NULL DEFAULT 10", cancellationToken);
        await AddColumnAsync(db, proxyColumns, "RtpType", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MediaKitProtocolPresets" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_MediaKitProtocolPresets" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "EnableAudio" INTEGER NOT NULL,
                "AddMuteAudio" INTEGER NOT NULL,
                "EnableHls" INTEGER NOT NULL,
                "EnableMp4" INTEGER NOT NULL,
                "EnableRtsp" INTEGER NOT NULL,
                "EnableRtmp" INTEGER NOT NULL,
                "EnableTs" INTEGER NOT NULL,
                "EnableFmp4" INTEGER NOT NULL,
                "AutoClose" INTEGER NOT NULL,
                "Mp4MaxSeconds" INTEGER NOT NULL,
                "Mp4SavePath" TEXT NOT NULL,
                "HlsSavePath" TEXT NOT NULL,
                "ExtraProperties" TEXT NOT NULL,
                "ConcurrencyStamp" TEXT NOT NULL,
                "CreationTime" TEXT NOT NULL,
                "CreatorId" TEXT NULL,
                "LastModificationTime" TEXT NULL,
                "LastModifierId" TEXT NULL,
                "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                "DeleterId" TEXT NULL,
                "DeletionTime" TEXT NULL
            );
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_MediaKitProtocolPresets_Name"
            ON "MediaKitProtocolPresets" ("Name");
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MediaKitPushProxies" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_MediaKitPushProxies" PRIMARY KEY,
                "MediaServerId" TEXT NOT NULL,
                "Schema" TEXT NOT NULL,
                "Vhost" TEXT NOT NULL,
                "App" TEXT NOT NULL,
                "Stream" TEXT NOT NULL,
                "DestinationUrl" TEXT NOT NULL,
                "Remark" TEXT NOT NULL DEFAULT '',
                "ProxyKey" TEXT NULL,
                "RetryCount" INTEGER NOT NULL DEFAULT -1,
                "TimeoutSeconds" INTEGER NOT NULL DEFAULT 10,
                "RtpType" INTEGER NOT NULL DEFAULT 0,
                "IsActive" INTEGER NOT NULL DEFAULT 0,
                "ExtraProperties" TEXT NOT NULL,
                "ConcurrencyStamp" TEXT NOT NULL,
                "CreationTime" TEXT NOT NULL,
                "CreatorId" TEXT NULL,
                "LastModificationTime" TEXT NULL,
                "LastModifierId" TEXT NULL,
                "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                "DeleterId" TEXT NULL,
                "DeletionTime" TEXT NULL
            );
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_MediaKitPushProxies_MediaServerId_Vhost_App_Stream_DestinationUrl"
            ON "MediaKitPushProxies" ("MediaServerId", "Vhost", "App", "Stream", "DestinationUrl");
            """, cancellationToken);
    }

    private static async Task<HashSet<string>> GetColumnsAsync(MediaKitDbContext db, string table, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(1));
        return result;
    }

    private static async Task AddColumnAsync(MediaKitDbContext db, HashSet<string> existing,
        string name, string definition, CancellationToken cancellationToken)
    {
        if (existing.Contains(name)) return;
        await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"MediaKitPullProxies\" ADD COLUMN \"{name}\" {definition}", cancellationToken);
        existing.Add(name);
    }
}
