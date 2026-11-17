namespace Apiext.MediaKit.Server.Persistence;

/// <summary>GB28181 注册上来的设备（NVR/IPC），记录信令地址与在线状态。</summary>
public class GbDevice
{
    public int Id { get; set; }

    /// <summary>设备国标编码（20 位）</summary>
    public string DeviceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>设备 SIP 信令源 IP（注册来源）</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>设备 SIP 信令端口</summary>
    public int Port { get; set; }

    /// <summary>是否在线（以最后心跳判定）</summary>
    public bool Online { get; set; }

    public DateTime RegisteredAt { get; set; }
    public DateTime LastKeepalive { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>设备下的通道（国标目录同步）。</summary>
    public List<GbChannel> Channels { get; set; } = new();
}
