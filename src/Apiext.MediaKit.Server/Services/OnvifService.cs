using Apiext.MediaKit.Server.Onvif;
using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>ONVIF 业务编排：发现、添加、取流、接入拉流代理。</summary>
public class OnvifService
{
    private readonly AppDbContext _db;
    private readonly PullProxyService _pullProxy;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OnvifService> _logger;

    public OnvifService(AppDbContext db, PullProxyService pullProxy, IHttpClientFactory httpFactory, ILogger<OnvifService> logger)
    {
        _db = db;
        _pullProxy = pullProxy;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>创建设备 SOAP 客户端（HttpClient 经工厂复用连接，不再累积泄漏）。</summary>
    private OnvifClient CreateClient(string host, int port, string username, string password)
        => new(host, port, username, password, _httpFactory.CreateClient("onvif"));

    /// <summary>WS-Discovery 发现局域网 ONVIF 设备。</summary>
    public Task<List<OnvifDiscoveredDevice>> DiscoverAsync(int timeoutSeconds = 3)
        => OnvifDiscovery.DiscoverAsync(timeoutSeconds);

    /// <summary>已接入的 ONVIF 设备列表。</summary>
    public Task<List<OnvifDevice>> ListAsync()
        => _db.OnvifDevices.OrderBy(d => d.CreatedAt).ToListAsync();

    /// <summary>测试设备连通性与认证（不落库，仅取设备信息）。</summary>
    public async Task<(bool Ok, string Message)> TestAsync(string host, int port, string username, string password)
    {
        try
        {
            var client = CreateClient(host, port, username, password);
            var info = await client.GetDeviceInformationAsync();
            if (info == null)
            {
                _logger.LogWarning("[Onvif] 测试设备失败：连接或认证不通过 {Host}:{Port}", host, port);
                return (false, "连接或认证失败（请确认 IP / 端口 / 账号密码，且设备已开启 ONVIF）");
            }

            var model = string.Join(" ", new[] { info.Manufacturer, info.Model }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            var fw = string.IsNullOrWhiteSpace(info.FirmwareVersion) ? string.Empty : $"，固件 {info.FirmwareVersion}";
            return (true, $"连接成功：{(string.IsNullOrWhiteSpace(model) ? "已识别设备" : model)}{fw}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Onvif] 测试设备异常 {Host}:{Port}", host, port);
            return (false, $"连接异常：{ex.Message}");
        }
    }

    /// <summary>手动添加设备（验证连接 + 获取设备信息）。</summary>
    /// <param name="force">强制添加：连接/认证不通过时仍保存设备，供稍后修正地址或账号密码。</param>
    public async Task<(bool Ok, string? Error, OnvifDevice? Device)> AddAsync(
        string host, int port, string username, string password, string? name = null, bool force = false)
    {
        OnvifDeviceInfo? info = null;
        try
        {
            var client = CreateClient(host, port, username, password);
            info = await client.GetDeviceInformationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Onvif] 添加设备时连接异常 {Host}:{Port}", host, port);
        }

        string? warning = null;
        if (info == null)
        {
            if (!force)
            {
                _logger.LogWarning("[Onvif] 添加设备失败：连接或认证不通过 {Host}:{Port}", host, port);
                return (false, "无法连接设备或认证失败（请确认 IP/端口/账号密码）", null);
            }

            warning = "未能连接设备，已强制保存（请稍后检查 IP / 端口 / 账号密码）";
            _logger.LogWarning("[Onvif] 强制添加未验证设备 {Host}:{Port}", host, port);
        }

        var device = new OnvifDevice
        {
            Name = !string.IsNullOrWhiteSpace(name)
                ? name!
                : (!string.IsNullOrWhiteSpace(info?.Model) ? info!.Model : host),
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            Manufacturer = info?.Manufacturer ?? string.Empty,
            Model = info?.Model ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
        };
        _db.OnvifDevices.Add(device);
        await _db.SaveChangesAsync();
        _logger.LogInformation("[Onvif] 已添加设备 {Name} {Host}:{Port} 型号={Model}",
            device.Name, device.Host, device.Port, device.Model);
        return (true, warning, device);
    }

    public async Task DeleteAsync(int id)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device != null)
        {
            _db.OnvifDevices.Remove(device);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>批量导入 ONVIF 设备：不做在线验证直接落库，按 host:port 去重。</summary>
    public async Task<(bool Ok, string? Error)> ImportAsync(
        string name, string host, int port, string username, string password,
        string manufacturer = "", string model = "")
    {
        if (string.IsNullOrWhiteSpace(host))
            return (false, "IP 地址为空");

        var exists = await _db.OnvifDevices.AnyAsync(d => d.Host == host && d.Port == port);
        if (exists) return (false, $"设备 {host}:{port} 已存在");

        _db.OnvifDevices.Add(new OnvifDevice
        {
            Name = string.IsNullOrWhiteSpace(name) ? (string.IsNullOrWhiteSpace(model) ? host : model) : name,
            Host = host,
            Port = port,
            Username = username ?? "",
            Password = password ?? "",
            Manufacturer = manufacturer ?? "",
            Model = model ?? "",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>获取设备 RTSP 流地址（GetProfiles + GetStreamUri）。</summary>
    public async Task<(bool Ok, string? Error, string? RtspUrl)> GetStreamUriAsync(int id)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在", null);

        var client = CreateClient(device.Host, device.Port, device.Username, device.Password);
        var profiles = await client.GetProfilesAsync();
        if (profiles.Count == 0)
        {
            _logger.LogWarning("[Onvif] 取流失败：未获取到媒体 Profile {Name} {Host}:{Port}", device.Name, device.Host, device.Port);
            return (false, "未获取到媒体 Profile", null);
        }

        var rtsp = await client.GetStreamUriAsync(profiles[0]);
        if (string.IsNullOrEmpty(rtsp))
        {
            _logger.LogWarning("[Onvif] 取流失败：GetStreamUri 返回空 {Name} {Host}:{Port}", device.Name, device.Host, device.Port);
            return (false, "获取流地址失败", null);
        }

        device.RtspUrl = rtsp;
        device.ProfileToken = profiles[0];
        await _db.SaveChangesAsync();
        _logger.LogInformation("[Onvif] 取流成功 {Name} {Host}:{Port}", device.Name, device.Host, device.Port);
        return (true, null, rtsp);
    }

    /// <summary>获取设备 JPEG 抓拍地址（GetSnapshotUri）。</summary>
    public async Task<(bool Ok, string? Error, string? SnapshotUrl)> GetSnapshotUriAsync(int id)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在", null);

        var ctx = await EnsureProfileTokenAsync(device);
        if (ctx == null) return (false, "未获取到媒体 Profile（请先「取流」）", null);

        var url = await ctx.Value.Client.GetSnapshotUriAsync(ctx.Value.ProfileToken);
        return string.IsNullOrEmpty(url)
            ? (false, "获取抓拍地址失败", null)
            : (true, null, url);
    }

    /// <summary>把 RTSP 流地址接入为拉流代理（复用播放/录像/按需拉流能力）。</summary>
    public async Task<(bool Ok, string? Error)> AttachProxyAsync(int id, string app, string stream)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在");
        if (string.IsNullOrEmpty(device.RtspUrl))
            return (false, "请先获取流地址");

        return await _pullProxy.AddAsync("__defaultVhost__", app, stream, device.RtspUrl, 1);
    }

    /// <summary>确保设备已有媒体 Profile token（PTZ 操作的前置条件）。</summary>
    private async Task<(OnvifClient Client, string ProfileToken)?> EnsureProfileTokenAsync(OnvifDevice device)
    {
        var client = CreateClient(device.Host, device.Port, device.Username, device.Password);

        if (string.IsNullOrEmpty(device.ProfileToken))
        {
            var profiles = await client.GetProfilesAsync();
            if (profiles.Count == 0) return null;
            device.ProfileToken = profiles[0];
            await _db.SaveChangesAsync();
        }

        return (client, device.ProfileToken);
    }

    /// <summary>
    /// 确保设备 PTZ 各轴方向已解析（首次移动时调用 GetConfigurationOptions 探测并缓存）。
    /// 探测失败时回退默认方向（不反转），下次移动会重试。
    /// </summary>
    private async Task<(OnvifClient Client, string ProfileToken, PtzAxes Axes)?> EnsurePtzAxesAsync(OnvifDevice device)
    {
        var ctx = await EnsureProfileTokenAsync(device);
        if (ctx == null) return null;

        var axes = new PtzAxes
        {
            PanReversed = device.PtzPanReversed,
            TiltReversed = device.PtzTiltReversed,
            ZoomReversed = device.PtzZoomReversed,
        };

        if (!device.PtzAxesResolved)
        {
            var configToken = await ctx.Value.Client.GetPtzConfigurationTokenAsync(ctx.Value.ProfileToken);
            if (!string.IsNullOrEmpty(configToken))
            {
                var parsed = await ctx.Value.Client.GetPtzAxesAsync(configToken);
                if (parsed != null)
                {
                    axes = parsed;
                    device.PtzPanReversed = parsed.PanReversed;
                    device.PtzTiltReversed = parsed.TiltReversed;
                    device.PtzZoomReversed = parsed.ZoomReversed;
                    device.PtzAxesResolved = true;
                    await _db.SaveChangesAsync();
                }
            }
        }

        return (ctx.Value.Client, ctx.Value.ProfileToken, axes);
    }

    /// <summary>获取设备的预置位列表。</summary>
    public async Task<(bool Ok, string? Error, List<OnvifPreset> Presets)> GetPresetsAsync(int id)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在", new List<OnvifPreset>());

        var ctx = await EnsureProfileTokenAsync(device);
        if (ctx == null) return (false, "未获取到媒体 Profile（请先「取流」）", new List<OnvifPreset>());

        var presets = await ctx.Value.Client.GetPresetsAsync(ctx.Value.ProfileToken);
        return (true, null, presets);
    }

    /// <summary>把当前云台位置保存为新预置位。</summary>
    public async Task<(bool Ok, string? Error)> SavePresetAsync(int id, string presetName)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在");

        var ctx = await EnsureProfileTokenAsync(device);
        if (ctx == null) return (false, "未获取到媒体 Profile（请先「取流」）");

        var token = await ctx.Value.Client.SetPresetAsync(ctx.Value.ProfileToken, presetName);
        return token != null ? (true, null) : (false, "保存预置位失败");
    }

    /// <summary>删除指定预置位。</summary>
    public async Task<(bool Ok, string? Error)> RemovePresetAsync(int id, string presetToken)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在");

        var ctx = await EnsureProfileTokenAsync(device);
        if (ctx == null) return (false, "未获取到媒体 Profile（请先「取流」）");

        var ok = await ctx.Value.Client.RemovePresetAsync(ctx.Value.ProfileToken, presetToken);
        return ok ? (true, null) : (false, "删除预置位失败");
    }

    /// <summary>跳转到指定预置位。</summary>
    public async Task<(bool Ok, string? Error)> GotoPresetAsync(int id, string presetToken)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在");

        var ctx = await EnsureProfileTokenAsync(device);
        if (ctx == null) return (false, "未获取到媒体 Profile（请先「取流」）");

        var ok = await ctx.Value.Client.GotoPresetAsync(ctx.Value.ProfileToken, presetToken);
        return ok ? (true, null) : (false, "跳转预置位失败");
    }

    /// <summary>按方向连续移动云台（自动适配设备轴方向）。</summary>
    public async Task<(bool Ok, string? Error)> PtzMoveAsync(int id, PtzDirection direction, double speed)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在");

        var ctx = await EnsurePtzAxesAsync(device);
        if (ctx == null) return (false, "未获取到媒体 Profile（请先「取流」）");

        var ok = await ctx.Value.Client.ContinuousMoveAsync(ctx.Value.ProfileToken, direction, speed, ctx.Value.Axes);
        return ok ? (true, null) : (false, "云台移动失败");
    }

    /// <summary>停止云台移动。</summary>
    public async Task<(bool Ok, string? Error)> PtzStopAsync(int id)
    {
        var device = await _db.OnvifDevices.FindAsync(id);
        if (device == null) return (false, "设备不存在");

        var ctx = await EnsureProfileTokenAsync(device);
        if (ctx == null) return (false, "未获取到媒体 Profile（请先「取流」）");

        var ok = await ctx.Value.Client.StopAsync(ctx.Value.ProfileToken);
        return ok ? (true, null) : (false, "停止失败");
    }
}
