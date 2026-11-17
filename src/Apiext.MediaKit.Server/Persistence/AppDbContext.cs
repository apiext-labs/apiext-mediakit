using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<PullProxy> PullProxies => Set<PullProxy>();
    public DbSet<GbDevice> GbDevices => Set<GbDevice>();
    public DbSet<GbChannel> GbChannels => Set<GbChannel>();
    public DbSet<GbAlarm> GbAlarms => Set<GbAlarm>();
    public DbSet<CascadeUpper> CascadeUppers => Set<CascadeUpper>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<OnvifDevice> OnvifDevices => Set<OnvifDevice>();
    public DbSet<BackupTarget> BackupTargets => Set<BackupTarget>();
    public DbSet<GbPresetDevice> GbPresetDevices => Set<GbPresetDevice>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<DeviceGroupMember> DeviceGroupMembers => Set<DeviceGroupMember>();
    public DbSet<RecordingFile> RecordingFiles => Set<RecordingFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PullProxy>()
            .HasIndex(p => new { p.Vhost, p.App, p.Stream })
            .IsUnique();

        modelBuilder.Entity<GbDevice>()
            .HasIndex(d => d.DeviceId)
            .IsUnique();

        modelBuilder.Entity<GbChannel>()
            .HasIndex(c => c.ChannelId)
            .IsUnique();

        // 通道 → 设备（按国标编码关联，级联删除），并给 DeviceId 补查询索引
        modelBuilder.Entity<GbChannel>()
            .HasOne(c => c.Device)
            .WithMany(d => d.Channels)
            .HasForeignKey(c => c.DeviceId)
            .HasPrincipalKey(d => d.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GbChannel>()
            .HasIndex(c => c.DeviceId);

        // 报警查询索引（按设备、按时间倒序取最近记录）
        modelBuilder.Entity<GbAlarm>()
            .HasIndex(a => a.DeviceId);
        modelBuilder.Entity<GbAlarm>()
            .HasIndex(a => a.AlarmTime);

        modelBuilder.Entity<CascadeUpper>()
            .HasIndex(c => new { c.Ip, c.Port })
            .IsUnique();

        modelBuilder.Entity<AppSetting>()
            .HasIndex(s => s.Key)
            .IsUnique();

        modelBuilder.Entity<BackupTarget>()
            .HasIndex(b => b.Name)
            .IsUnique();

        modelBuilder.Entity<GbPresetDevice>()
            .HasIndex(d => d.DeviceId)
            .IsUnique();

        modelBuilder.Entity<ApiToken>()
            .HasIndex(t => t.Token)
            .IsUnique();

        modelBuilder.Entity<DeviceGroup>()
            .HasIndex(g => g.Name)
            .IsUnique();

        // 同一设备只能归属一个分组；按分组查成员需 GroupId 索引
        modelBuilder.Entity<DeviceGroupMember>()
            .HasIndex(m => new { m.Kind, m.App, m.Stream })
            .IsUnique();
        modelBuilder.Entity<DeviceGroupMember>()
            .HasIndex(m => m.GroupId);

        // 录像索引：hook 可能重复投递（retry），按 App+Stream+FileName 唯一保证幂等
        modelBuilder.Entity<RecordingFile>()
            .HasIndex(r => new { r.App, r.Stream, r.FileName })
            .IsUnique();
        modelBuilder.Entity<RecordingFile>()
            .HasIndex(r => r.StartTimeUtc);
    }
}
