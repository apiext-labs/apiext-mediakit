using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>
/// GB28181 设备 / 通道 / 报警落库。
/// 从 SIP 信令引擎中抽离，避免信令处理逻辑与 EF Core 混杂，便于单独演进。
/// </summary>
public class GbDeviceStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GbDeviceStore> _logger;

    public GbDeviceStore(IServiceScopeFactory scopeFactory, ILogger<GbDeviceStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>注册 / 心跳时更新设备在线信息（不存在则新建）。</summary>
    public async Task UpsertOnlineAsync(string deviceId, string host, int port, string? name)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.GbDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device == null)
            {
                device = new GbDevice
                {
                    DeviceId = deviceId,
                    Name = string.IsNullOrEmpty(name) ? deviceId : name,
                    Host = host,
                    Port = port,
                    Online = true,
                    RegisteredAt = DateTime.UtcNow,
                    LastKeepalive = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                };
                db.GbDevices.Add(device);
            }
            else
            {
                device.Host = host;
                device.Port = port;
                device.Online = true;
                device.LastKeepalive = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GB28181] 持久化设备失败 {Id}", deviceId);
        }
    }

    /// <summary>设备是否已在库中（平台重启后内存状态丢失，可凭心跳恢复在线）。</summary>
    public async Task<bool> ExistsAsync(string deviceId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.GbDevices.AnyAsync(d => d.DeviceId == deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GB28181] 查询设备失败 {Id}", deviceId);
            return false;
        }
    }

    /// <summary>标记设备离线。</summary>
    public async Task MarkOfflineAsync(string deviceId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.GbDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device != null)
            {
                device.Online = false;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GB28181] 标记设备离线失败 {Id}", deviceId);
        }
    }

    /// <summary>目录同步：新增 / 更新通道，并同步设备名称。</summary>
    public async Task SaveChannelsAsync(string deviceId, List<GbCatalogItem> items)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var device = await db.GbDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device != null)
            {
                device.Name = items.FirstOrDefault(i => i.DeviceId == deviceId)?.Name ?? device.Name;
            }

            foreach (var item in items)
            {
                var channel = await db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == item.DeviceId);
                if (channel == null)
                {
                    db.GbChannels.Add(new GbChannel
                    {
                        ChannelId = item.DeviceId,
                        DeviceId = deviceId,
                        Name = item.Name,
                        Manufacturer = item.Manufacturer,
                        Model = item.Model,
                        Owner = item.Owner,
                        CivilCode = item.CivilCode,
                        Address = item.Address,
                        Parental = item.Parental,
                        ParentId = item.ParentId,
                        Status = item.Status,
                        CreatedAt = DateTime.UtcNow,
                    });
                }
                else
                {
                    channel.Name = item.Name;
                    channel.Manufacturer = item.Manufacturer;
                    channel.Model = item.Model;
                    channel.Owner = item.Owner;
                    channel.CivilCode = item.CivilCode;
                    channel.Address = item.Address;
                    channel.Parental = item.Parental;
                    channel.ParentId = item.ParentId;
                    channel.Status = item.Status;
                }
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GB28181] 持久化通道失败 {Id}", deviceId);
        }
    }

    /// <summary>保存设备上报的报警。</summary>
    public async Task SaveAlarmAsync(GbAlarm alarm)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GbAlarms.Add(alarm);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GB28181] 持久化报警失败 {Dev}", alarm.DeviceId);
        }
    }
}
