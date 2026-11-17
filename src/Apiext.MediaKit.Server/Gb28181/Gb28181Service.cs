using System.Net;
using Apiext.MediaKit.Server.Apis;
using Apiext.MediaKit.Server.Persistence;
using Apiext.MediaKit.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>GB28181 设备展示行。</summary>
public class GbDeviceRow
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool Online { get; set; }
    public int ChannelCount { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime LastKeepalive { get; set; }
    public string StatusText => Online ? "在线" : "离线";
}

/// <summary>GB28181 业务编排：设备/通道查询、目录查询、实时点播。</summary>
public class Gb28181Service
{
    private readonly AppDbContext _db;
    private readonly ZlmService _zlm;
    private readonly GbSipEngine _sip;
    private readonly GbCascadeEngine _cascade;
    private readonly AppSettingsService _settings;
    private readonly ILogger<Gb28181Service> _logger;

    public Gb28181Service(AppDbContext db, ZlmService zlm, GbSipEngine sip, GbCascadeEngine cascade,
        AppSettingsService settings, ILogger<Gb28181Service> logger)
    {
        _db = db;
        _zlm = zlm;
        _sip = sip;
        _cascade = cascade;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>设备列表（DB 记录 + 内存在线状态合并）。</summary>
    public async Task<List<GbDeviceRow>> GetDevicesAsync()
    {
        var onlineMap = _sip.OnlineDevices.ToDictionary(s => s.DeviceId, s => s, StringComparer.OrdinalIgnoreCase);

        var devices = await _db.GbDevices.OrderBy(d => d.CreatedAt).ToListAsync();
        var channelCounts = await _db.GbChannels
            .GroupBy(c => c.DeviceId)
            .Select(g => new { DeviceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DeviceId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        return devices.Select(d =>
        {
            var online = onlineMap.TryGetValue(d.DeviceId, out var state);
            return new GbDeviceRow
            {
                DeviceId = d.DeviceId,
                Name = d.Name,
                Host = online ? state!.Host : d.Host,
                Port = online ? state!.Port : d.Port,
                Online = online,
                ChannelCount = channelCounts.TryGetValue(d.DeviceId, out var c) ? c : 0,
                RegisteredAt = d.RegisteredAt,
                LastKeepalive = online ? state!.LastKeepalive : d.LastKeepalive,
            };
        }).ToList();
    }

    /// <summary>某设备下的通道列表。</summary>
    public async Task<List<GbChannel>> GetChannelsAsync(string deviceId)
        => await _db.GbChannels.Where(c => c.DeviceId == deviceId)
            .OrderBy(c => c.ChannelId).ToListAsync();

    // ---------- 接入模式与预登记（白名单鉴权） ----------

    /// <summary>接入模式：auto（注册即接入）| whitelist（预登记 + 白名单鉴权）。</summary>
    public Task<string?> GetAccessModeAsync() => _settings.GetAsync("gb28181.access.mode");

    public Task SetAccessModeAsync(string mode)
        => _settings.SetAsync("gb28181.access.mode", mode is "whitelist" or "auto" ? mode : "auto");

    /// <summary>预登记设备列表。</summary>
    public Task<List<GbPresetDevice>> GetPresetDevicesAsync()
        => _db.GbPresetDevices.OrderBy(p => p.CreatedAt).ToListAsync();

    /// <summary>新增预登记设备（国标 ID 唯一，转大写存储）。</summary>
    public async Task<(bool Ok, string? Error)> AddPresetDeviceAsync(string deviceId, string name, string password)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return (false, "国标 ID 不能为空");

        deviceId = deviceId.Trim().ToUpperInvariant();
        if (await _db.GbPresetDevices.AnyAsync(p => p.DeviceId == deviceId))
            return (false, "该国标 ID 已预登记");

        _db.GbPresetDevices.Add(new GbPresetDevice
        {
            DeviceId = deviceId,
            Name = string.IsNullOrWhiteSpace(name) ? deviceId : name.Trim(),
            Password = password?.Trim() ?? "",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>删除预登记设备。</summary>
    public async Task DeletePresetDeviceAsync(int id)
    {
        var entity = await _db.GbPresetDevices.FindAsync(id);
        if (entity != null)
        {
            _db.GbPresetDevices.Remove(entity);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>查询设备目录（发 SIP 目录查询，等待设备回传并落库）。</summary>
    public async Task<(bool Ok, string? Error)> QueryCatalogAsync(string deviceId)
    {
        var (ok, error, items) = await _sip.QueryCatalogAsync(deviceId);
        if (!ok)
        {
            _logger.LogWarning("[GB28181] 目录查询失败 设备={Device} 原因={Error}", deviceId, error);
            return (false, error);
        }
        _logger.LogInformation("[GB28181] 目录查询成功 设备={Device} 通道 {Count} 条", deviceId, items?.Count ?? 0);
        return (true, null);
    }

    /// <summary>实时点播：ZLM 开 RTP 端口 → SIP INVITE → 返回播放地址。</summary>
    public async Task<(bool Ok, string? Error, string? PlayUrl)> StartPlayAsync(string channelId)
    {
        var channel = await _db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (channel == null)
        {
            _logger.LogWarning("[GB28181] 点播失败：通道不存在 {Channel}", channelId);
            return (false, "通道不存在", null);
        }

        var deviceId = channel.DeviceId;
        if (_sip.GetDevice(deviceId) == null)
        {
            _logger.LogWarning("[GB28181] 点播失败：设备离线 设备={Device} 通道={Channel}", deviceId, channelId);
            return (false, "设备离线", null);
        }

        // 1. ZLM 打开 RTP 收流端口（stream_id 用通道编码）
        OpenRtpServerResult rtp;
        try
        {
            rtp = await _zlm.OpenRtpServerAsync(channelId, 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GB28181] 点播失败：ZLM 开收流异常 通道={Channel}", channelId);
            return (false, $"ZLM 开启收流失败：{ex.Message}", null);
        }
        if (rtp.Code != 0 || rtp.Port <= 0)
        {
            _logger.LogWarning("[GB28181] 点播失败：ZLM 开收流被拒 通道={Channel} code={Code} msg={Msg}",
                channelId, rtp.Code, rtp.Msg);
            return (false, $"ZLM 开启收流失败：{rtp.Msg}", null);
        }

        // 2. 生成 SSRC（国标实时流首位为 0）
        var ssrc = GbSsrc.NewLive();

        // 3. SIP INVITE 点播（stream_id 用通道编码）
        var (ok, error) = await _sip.InviteAsync(deviceId, channelId, channelId, rtp.Port, ssrc);
        if (!ok)
        {
            _logger.LogWarning("[GB28181] 点播失败：INVITE 未成功 设备={Device} 通道={Channel} 收流端口={Port} 原因={Error}",
                deviceId, channelId, rtp.Port, error);
            // 点播失败，回收 RTP 端口
            try { await _zlm.CloseRtpServerAsync(channelId); } catch { }
            return (false, error, null);
        }

        _logger.LogInformation("[GB28181] 点播成功 设备={Device} 通道={Channel} 收流端口={Port} SSRC={Ssrc}",
            deviceId, channelId, rtp.Port, ssrc);
        return (true, null, _zlm.BuildRtpFlvUrl(channelId));
    }

    /// <summary>停止点播：SIP BYE + ZLM 关闭收流端口。</summary>
    public async Task StopPlayAsync(string channelId)
    {
        await _sip.StopPlayAsync(channelId);
        try { await _zlm.CloseRtpServerAsync(channelId); } catch { }
    }

    /// <summary>云台控制（PTZ）。</summary>
    public async Task PtzControlAsync(string channelId, PtzCommand command, int param = 5)
    {
        var channel = await _db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (channel == null) return;
        await _sip.PtzControlAsync(channel.DeviceId, channelId, command, param);
    }

    /// <summary>国标网络校时：向通道下发校时 DeviceControl 指令。</summary>
    public Task TimeSyncAsync(string channelId)
        => PtzControlAsync(channelId, PtzCommand.TimeSync, 0);

    /// <summary>获取通道的多协议播放地址集合（RTSP/RTMP/HLS/FLV/WS-FLV）。</summary>
    public PlayAddresses GetPlayUrls(string channelId)
        => _zlm.BuildPlayAddresses("rtp", channelId);

    /// <summary>获取与指定通道同设备的其他通道（用于主/子码流切换）。</summary>
    public async Task<List<GbChannel>> GetSiblingChannelsAsync(string channelId)
    {
        var channel = await _db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (channel == null) return new List<GbChannel>();

        return await _db.GbChannels
            .Where(c => c.DeviceId == channel.DeviceId)
            .OrderBy(c => c.ChannelId)
            .ToListAsync();
    }

    /// <summary>录像查询：发 SIP 录像查询，返回录像文件列表。</summary>
    public async Task<(bool Ok, string? Error, List<GbRecordItem>? Records)> RecordQueryAsync(
        string channelId, DateTime start, DateTime end)
    {
        var channel = await _db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (channel == null)
        {
            _logger.LogWarning("[GB28181] 录像查询失败：通道不存在 {Channel}", channelId);
            return (false, "通道不存在", null);
        }
        if (_sip.GetDevice(channel.DeviceId) == null)
        {
            _logger.LogWarning("[GB28181] 录像查询失败：设备离线 设备={Device} 通道={Channel}", channel.DeviceId, channelId);
            return (false, "设备离线", null);
        }

        var result = await _sip.RecordQueryAsync(channel.DeviceId, channelId, start, end);
        if (!result.Ok)
            _logger.LogWarning("[GB28181] 录像查询失败 设备={Device} 通道={Channel} 区间={Start}~{End} 原因={Error}",
                channel.DeviceId, channelId, start, end, result.Error);
        else
            _logger.LogInformation("[GB28181] 录像查询完成 通道={Channel} 记录 {Count} 条", channelId, result.Records?.Count ?? 0);
        return result;
    }

    /// <summary>回放点播：ZLM 开 RTP 端口 → SIP 回放 INVITE → 返回播放地址。</summary>
    public async Task<(bool Ok, string? Error, string? PlayUrl, string? StreamId)> StartPlaybackAsync(
        string channelId, GbRecordItem record)
    {
        var channel = await _db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (channel == null)
        {
            _logger.LogWarning("[GB28181] 回放失败：通道不存在 {Channel}", channelId);
            return (false, "通道不存在", null, null);
        }

        var deviceId = channel.DeviceId;
        if (_sip.GetDevice(deviceId) == null)
        {
            _logger.LogWarning("[GB28181] 回放失败：设备离线 设备={Device} 通道={Channel}", deviceId, channelId);
            return (false, "设备离线", null, null);
        }

        if (!DateTime.TryParse(record.StartTime, out var start) || !DateTime.TryParse(record.EndTime, out var end))
        {
            _logger.LogWarning("[GB28181] 回放失败：录像时间解析异常 通道={Channel} start={Start} end={End}",
                channelId, record.StartTime, record.EndTime);
            return (false, "录像时间解析失败", null, null);
        }

        // 回放 SSRC：首位为 1（国标回放流）
        var ssrc = GbSsrc.NewPlayback();

        // ZLM 开 RTP 收流端口（stream_id 用 SSRC，避免与实时流冲突）
        OpenRtpServerResult rtp;
        try
        {
            rtp = await _zlm.OpenRtpServerAsync(ssrc, 0, 0);
        }
        catch (Exception ex)
        {
            return (false, $"ZLM 开启收流失败：{ex.Message}", null, null);
        }
        if (rtp.Code != 0 || rtp.Port <= 0)
        {
            _logger.LogWarning("[GB28181] 回放失败：ZLM 开收流被拒 通道={Channel} code={Code} msg={Msg}",
                channelId, rtp.Code, rtp.Msg);
            return (false, $"ZLM 开启收流失败：{rtp.Msg}", null, null);
        }

        var (ok, error) = await _sip.InviteAsync(deviceId, channelId, ssrc, rtp.Port, ssrc, true, start, end);
        if (!ok)
        {
            _logger.LogWarning("[GB28181] 回放失败：INVITE 未成功 设备={Device} 通道={Channel} 区间={Start}~{End} 原因={Error}",
                deviceId, channelId, start, end, error);
            try { await _zlm.CloseRtpServerAsync(ssrc); } catch { }
            return (false, error, null, null);
        }

        _logger.LogInformation("[GB28181] 回放点播成功 设备={Device} 通道={Channel} SSRC={Ssrc} 区间={Start}~{End}",
            deviceId, channelId, ssrc, start, end);
        return (true, null, _zlm.BuildRtpFlvUrl(ssrc), ssrc);
    }

    /// <summary>回放控制（暂停/继续/拖动）：发 INFO。</summary>
    public async Task PlaybackControlAsync(string streamId, long rangeTime, bool pause = false)
        => await _sip.PlaybackControlAsync(streamId, rangeTime, pause);

    /// <summary>停止回放：SIP BYE + ZLM 关闭收流端口。</summary>
    public async Task StopPlaybackAsync(string streamId)
    {
        await _sip.StopPlayAsync(streamId);
        try { await _zlm.CloseRtpServerAsync(streamId); } catch { }
    }

    /// <summary>最近的报警记录。</summary>
    public async Task<List<GbAlarm>> GetAlarmsAsync(int limit = 100)
        => await _db.GbAlarms.OrderByDescending(a => a.AlarmTime).Take(limit).ToListAsync();

    /// <summary>订阅通道报警。</summary>
    public async Task SubscribeAlarmAsync(string channelId)
    {
        var channel = await _db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (channel == null) return;
        await _sip.SubscribeAlarmAsync(channel.DeviceId, channelId);
    }

    /// <summary>播放地址（HTTP-FLV）。</summary>
    public string GetPlayUrl(string channelId) => _zlm.BuildRtpFlvUrl(channelId);

    // ---- 平台级联（上级平台动态管理）----

    /// <summary>上级平台列表（持久化记录）。</summary>
    public async Task<List<CascadeUpper>> GetCascadeUppersAsync()
        => await _db.CascadeUppers.OrderBy(u => u.Id).ToListAsync();

    /// <summary>新增上级平台（落库后触发级联引擎热重载）。</summary>
    public async Task<(bool Ok, string? Error, CascadeUpper? Upper)> AddCascadeUpperAsync(CascadeUpper upper)
    {
        if (string.IsNullOrWhiteSpace(upper.Ip) || string.IsNullOrWhiteSpace(upper.PlatformId))
            return (false, "IP 地址与上级平台编码为必填项", null);
        if (!IPAddress.TryParse(upper.Ip, out _))
            return (false, "IP 地址无效", null);

        var exists = await _db.CascadeUppers.AnyAsync(u => u.Ip == upper.Ip && u.Port == upper.Port);
        if (exists) return (false, "该 IP:端口 的上级平台已存在", null);

        upper.CreatedAt = DateTime.UtcNow;
        _db.CascadeUppers.Add(upper);
        await _db.SaveChangesAsync();

        await _cascade.ReloadAsync();
        return (true, null, upper);
    }

    /// <summary>更新上级平台（落库后触发级联引擎热重载）。</summary>
    public async Task<(bool Ok, string? Error)> UpdateCascadeUpperAsync(int id, CascadeUpper upper)
    {
        var existing = await _db.CascadeUppers.FindAsync(id);
        if (existing == null) return (false, "上级平台不存在");

        if (string.IsNullOrWhiteSpace(upper.Ip) || string.IsNullOrWhiteSpace(upper.PlatformId))
            return (false, "IP 地址与上级平台编码为必填项");
        if (!IPAddress.TryParse(upper.Ip, out _))
            return (false, "IP 地址无效");

        var dup = await _db.CascadeUppers.AnyAsync(u => u.Id != id && u.Ip == upper.Ip && u.Port == upper.Port);
        if (dup) return (false, "该 IP:端口 的上级平台已存在");

        existing.Name = upper.Name;
        existing.Ip = upper.Ip;
        existing.Port = upper.Port;
        existing.PlatformId = upper.PlatformId;
        existing.Domain = upper.Domain;
        existing.Realm = upper.Realm;
        existing.Password = upper.Password;
        await _db.SaveChangesAsync();

        await _cascade.ReloadAsync();
        return (true, null);
    }

    /// <summary>删除上级平台（落库后触发级联引擎热重载）。</summary>
    public async Task<(bool Ok, string? Error)> DeleteCascadeUpperAsync(int id)
    {
        var existing = await _db.CascadeUppers.FindAsync(id);
        if (existing == null) return (false, "上级平台不存在");

        _db.CascadeUppers.Remove(existing);
        await _db.SaveChangesAsync();

        await _cascade.ReloadAsync();
        return (true, null);
    }
}
