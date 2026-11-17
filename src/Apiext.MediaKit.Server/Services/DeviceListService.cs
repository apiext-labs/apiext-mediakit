using System.Text.Json;
using Apiext.MediaKit.Server.Gb28181;
using Apiext.MediaKit.Server.Onvif;

namespace Apiext.MediaKit.Server.Services;

/// <summary>统一设备展示行（跨 PULL/RTSP/RTMP/HTTP-FLV/ONVIF/GB28181 协议汇总）。</summary>
public class UnifiedDeviceRow
{
    /// <summary>唯一键，如 pull:live/cam1、onvif:1、gb:3402...。</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>协议：RTSP / RTMP / HTTP-FLV / ONVIF / GB28181。</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>在线状态；null 表示未知（如 ONVIF 未探测）。</summary>
    public bool? Online { get; set; }

    /// <summary>地址（主机/IP:端口）或源流主机。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>补充信息（源地址 / 厂商型号 / 通道数）。</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>取流模式（仅拉流代理：native / ffmpeg）。</summary>
    public string PullMode { get; set; } = string.Empty;

    /// <summary>
    /// 封面快照地址（仅在线拉流代理可得；null 表示无快照，前端展示默认封面）。
    /// 该 URL 带 ZLM secret，只在服务端渲染时用，禁止出现在对外 REST API（/api/v1/devices）的 JSON 里。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SnapshotUrl { get; set; }

    /// <summary>ZLM 虚拟主机（批量删除拉流代理用）。</summary>
    public string Vhost { get; set; } = "__defaultVhost__";

    /// <summary>应用名（仅拉流代理：播放直达、批量操作）。</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>流 ID（仅拉流代理：播放直达、批量操作）。</summary>
    public string Stream { get; set; } = string.Empty;

    /// <summary>ONVIF 设备主键（批量删除用）。</summary>
    public int? OnvifId { get; set; }

    /// <summary>码率文案（仅在线拉流代理，如 "1.2 Mbps"）。</summary>
    public string SpeedText { get; set; } = string.Empty;

    /// <summary>流存活秒数（仅在线拉流代理，卡片角标显示在线时长）。</summary>
    public long AliveSecond { get; set; }

    /// <summary>是否正在录像（仅拉流代理）。</summary>
    public bool IsRecording { get; set; }
}

/// <summary>设备列表导入导出文件（含可手动管理的拉流代理与 ONVIF 设备；GB28181 设备由设备侧主动注册，不参与）。</summary>
public class DeviceExportFile
{
    public int Version { get; set; } = 1;
    public List<ProxyExportItem> Proxies { get; set; } = new();
    public List<OnvifExportItem> OnvifDevices { get; set; } = new();
}

/// <summary>拉流代理导出项。</summary>
public class ProxyExportItem
{
    public string App { get; set; } = string.Empty;
    public string Stream { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string PullMode { get; set; } = "native";
    public string OutputCodec { get; set; } = "copy";
    public bool OnDemand { get; set; }
    public bool Record { get; set; }
}

/// <summary>ONVIF 设备导出项。</summary>
public class OnvifExportItem
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 80;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}

/// <summary>统一设备列表：汇总拉流代理、ONVIF、GB28181 三类设备。</summary>
public class DeviceListService
{
    private readonly PullProxyService _pull;
    private readonly OnvifService _onvif;
    private readonly Gb28181Service _gb;
    private readonly ZlmService _zlm;

    public DeviceListService(PullProxyService pull, OnvifService onvif, Gb28181Service gb, ZlmService zlm)
    {
        _pull = pull;
        _onvif = onvif;
        _gb = gb;
        _zlm = zlm;
    }

    public async Task<List<UnifiedDeviceRow>> ListAsync()
    {
        var result = new List<UnifiedDeviceRow>();

        // 1. 拉流代理（RTSP / RTMP / HTTP-FLV）
        var proxies = await _pull.ListAsync();
        foreach (var p in proxies)
        {
            var proto = StreamProtocol.Detect(p.Url);
            result.Add(new UnifiedDeviceRow
            {
                Key = $"pull:{p.App}/{p.Stream}",
                Name = $"{p.App}/{p.Stream}",
                Protocol = proto,
                Online = p.IsOnline,
                Address = SafeHost(p.Url) ?? p.Url,
                Detail = p.Url,
                PullMode = p.PullMode,
                Vhost = p.Vhost,
                App = p.App,
                Stream = p.Stream,
                SpeedText = p.IsOnline ? p.SpeedText : string.Empty,
                AliveSecond = p.IsOnline ? p.AliveSecond : 0,
                IsRecording = p.IsRecording,
                // 快照只有流在线时 ZLM 才能抓出来；抓图缓存 300s，避免列表轮询反复触发解码
                SnapshotUrl = p.IsOnline
                    ? _zlm.BuildSnapUrl(p.App, p.Stream, timeoutSec: 3, expireSec: 300)
                    : null,
            });
        }

        // 2. ONVIF 设备（在线状态未探测，置 null）
        var onvifs = await _onvif.ListAsync();
        foreach (var o in onvifs)
        {
            result.Add(new UnifiedDeviceRow
            {
                Key = $"onvif:{o.Id}",
                Name = string.IsNullOrEmpty(o.Name) ? o.Host : o.Name,
                Protocol = "ONVIF",
                Online = null,
                Address = $"{o.Host}:{o.Port}",
                Detail = $"{o.Manufacturer} {o.Model}".Trim(),
                OnvifId = o.Id,
            });
        }

        // 3. GB28181 设备
        var gbDevices = await _gb.GetDevicesAsync();
        foreach (var g in gbDevices)
        {
            result.Add(new UnifiedDeviceRow
            {
                Key = $"gb:{g.DeviceId}",
                Name = string.IsNullOrEmpty(g.Name) ? g.DeviceId : g.Name,
                Protocol = "GB28181",
                Online = g.Online,
                Address = $"{g.Host}:{g.Port}",
                Detail = $"通道 {g.ChannelCount}",
            });
        }

        return result;
    }

    /// <summary>导出可手动管理的设备（拉流代理 + ONVIF）为 JSON 字符串。</summary>
    public async Task<string> ExportAsync()
    {
        var file = new DeviceExportFile();

        foreach (var p in await _pull.ListAsync())
        {
            file.Proxies.Add(new ProxyExportItem
            {
                App = p.App,
                Stream = p.Stream,
                Url = p.Url,
                PullMode = p.PullMode,
                OutputCodec = p.OutputCodec,
                OnDemand = p.EnableOnDemand,
                Record = p.EnableRecord,
            });
        }

        foreach (var o in await _onvif.ListAsync())
        {
            file.OnvifDevices.Add(new OnvifExportItem
            {
                Name = o.Name,
                Host = o.Host,
                Port = o.Port,
                Username = o.Username,
                Password = o.Password,
                Manufacturer = o.Manufacturer,
                Model = o.Model,
            });
        }

        return JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>从 JSON 导入设备，返回（成功数, 失败数, 失败原因列表）。</summary>
    public async Task<(int Added, int Failed, List<string> Errors)> ImportAsync(string json)
    {
        var added = 0;
        var failed = 0;
        var errors = new List<string>();

        DeviceExportFile file;
        try
        {
            file = JsonSerializer.Deserialize<DeviceExportFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new DeviceExportFile();
        }
        catch (Exception ex)
        {
            return (0, 1, new List<string> { $"文件解析失败：{ex.Message}" });
        }

        foreach (var p in file.Proxies ?? new List<ProxyExportItem>())
        {
            if (string.IsNullOrWhiteSpace(p.App) || string.IsNullOrWhiteSpace(p.Stream) || string.IsNullOrWhiteSpace(p.Url))
            {
                failed++;
                errors.Add($"拉流代理 [{p.App ?? ""}/{p.Stream ?? ""}]：应用名/流 ID/地址不能为空，已跳过");
                continue;
            }

            var (ok, error) = await _pull.AddAsync("__defaultVhost__", p.App, p.Stream, p.Url, null, p.OnDemand, p.Record, p.PullMode, p.OutputCodec);
            if (ok)
            {
                added++;
            }
            else
            {
                failed++;
                errors.Add($"拉流代理 [{p.App}/{p.Stream}]：{error}");
            }
        }

        foreach (var o in file.OnvifDevices ?? new List<OnvifExportItem>())
        {
            var (ok, error) = await _onvif.ImportAsync(o.Name, o.Host, o.Port, o.Username, o.Password, o.Manufacturer, o.Model);
            if (ok)
            {
                added++;
            }
            else
            {
                failed++;
                errors.Add($"ONVIF 设备 [{o.Host}:{o.Port}]：{error}");
            }
        }

        return (added, failed, errors);
    }

    private static string? SafeHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return null;
        }
    }
}
