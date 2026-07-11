using Apiext.MediaKit.Domain;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Apiext.MediaKit.EntityFrameworkCore;

[ConnectionStringName("Default")]
public sealed class MediaKitDbContext : AbpDbContext<MediaKitDbContext>
{
    public DbSet<MediaServer> MediaServers => Set<MediaServer>();
    public DbSet<PullProxy> PullProxies => Set<PullProxy>();
    public DbSet<PushProxy> PushProxies => Set<PushProxy>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<ProtocolPreset> ProtocolPresets => Set<ProtocolPreset>();

    public MediaKitDbContext(DbContextOptions<MediaKitDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<MediaServer>(b =>
        {
            b.ToTable("MediaKitServers");
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.BaseUrl).HasMaxLength(512).IsRequired();
            b.Property(x => x.Secret).HasMaxLength(256);
        });
        builder.Entity<PullProxy>(b =>
        {
            b.ToTable("MediaKitPullProxies");
            b.Property(x => x.Vhost).HasMaxLength(128).IsRequired();
            b.Property(x => x.App).HasMaxLength(128).IsRequired();
            b.Property(x => x.Stream).HasMaxLength(256).IsRequired();
            b.Property(x => x.SourceUrl).HasMaxLength(2048).IsRequired();
            b.Property(x => x.SourceUrlsJson).IsRequired();
            b.Property(x => x.Remark).HasMaxLength(512);
            b.Property(x => x.ProxyKey).HasMaxLength(256);
            b.HasIndex(x => new { x.MediaServerId, x.Vhost, x.App, x.Stream }).IsUnique();
        });
        builder.Entity<ProtocolPreset>(b =>
        {
            b.ToTable("MediaKitProtocolPresets");
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.Mp4SavePath).HasMaxLength(1024);
            b.Property(x => x.HlsSavePath).HasMaxLength(1024);
            b.HasIndex(x => x.Name).IsUnique();
        });
        builder.Entity<PushProxy>(b =>
        {
            b.ToTable("MediaKitPushProxies");
            b.Property(x => x.Schema).HasMaxLength(32).IsRequired();
            b.Property(x => x.Vhost).HasMaxLength(128).IsRequired();
            b.Property(x => x.App).HasMaxLength(128).IsRequired();
            b.Property(x => x.Stream).HasMaxLength(256).IsRequired();
            b.Property(x => x.DestinationUrl).HasMaxLength(2048).IsRequired();
            b.Property(x => x.Remark).HasMaxLength(512);
            b.Property(x => x.ProxyKey).HasMaxLength(256);
            b.HasIndex(x => new { x.MediaServerId, x.Vhost, x.App, x.Stream, x.DestinationUrl });
        });
        builder.Entity<WebhookEvent>(b =>
        {
            b.ToTable("MediaKitWebhookEvents");
            b.Property(x => x.EventName).HasMaxLength(128).IsRequired();
            b.Property(x => x.Vhost).HasMaxLength(128);
            b.Property(x => x.App).HasMaxLength(128);
            b.Property(x => x.Stream).HasMaxLength(256);
            b.Property(x => x.Payload).IsRequired();
            b.HasIndex(x => x.ReceivedAt);
        });
    }
}
