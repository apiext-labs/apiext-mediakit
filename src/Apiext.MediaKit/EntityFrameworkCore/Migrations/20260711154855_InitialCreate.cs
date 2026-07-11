using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apiext.MediaKit.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaKitProtocolPresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EnableAudio = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddMuteAudio = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableHls = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableMp4 = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableRtsp = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableRtmp = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableTs = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableFmp4 = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoClose = table.Column<bool>(type: "INTEGER", nullable: false),
                    Mp4MaxSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Mp4SavePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    HlsSavePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ExtraProperties = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaKitProtocolPresets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaKitPullProxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Vhost = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    App = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Stream = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SourceUrlsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ProxyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ProtocolPresetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OnDemand = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RtpType = table.Column<int>(type: "INTEGER", nullable: false),
                    EnableHls = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableMp4 = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExtraProperties = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaKitPullProxies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaKitPushProxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Schema = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Vhost = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    App = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Stream = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DestinationUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Remark = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ProxyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RtpType = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExtraProperties = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaKitPushProxies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaKitServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Secret = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExtraProperties = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaKitServers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaKitWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MediaServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Vhost = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    App = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Stream = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExtraProperties = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaKitWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaKitProtocolPresets_Name",
                table: "MediaKitProtocolPresets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaKitPullProxies_MediaServerId_Vhost_App_Stream",
                table: "MediaKitPullProxies",
                columns: new[] { "MediaServerId", "Vhost", "App", "Stream" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaKitPushProxies_MediaServerId_Vhost_App_Stream_DestinationUrl",
                table: "MediaKitPushProxies",
                columns: new[] { "MediaServerId", "Vhost", "App", "Stream", "DestinationUrl" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaKitWebhookEvents_ReceivedAt",
                table: "MediaKitWebhookEvents",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaKitProtocolPresets");

            migrationBuilder.DropTable(
                name: "MediaKitPullProxies");

            migrationBuilder.DropTable(
                name: "MediaKitPushProxies");

            migrationBuilder.DropTable(
                name: "MediaKitServers");

            migrationBuilder.DropTable(
                name: "MediaKitWebhookEvents");
        }
    }
}
