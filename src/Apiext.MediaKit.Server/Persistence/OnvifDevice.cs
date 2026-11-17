namespace Apiext.MediaKit.Server.Persistence;

/// <summary>ONVIF 接入的设备记录。</summary>
public class OnvifDevice
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 80;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>最近取到的 RTSP 流地址</summary>
    public string? RtspUrl { get; set; }

    /// <summary>最近取到的媒体 Profile token</summary>
    public string? ProfileToken { get; set; }

    /// <summary>PTZ 各轴方向是否已从设备解析并缓存</summary>
    public bool PtzAxesResolved { get; set; }

    /// <summary>Pan（X）轴方向反转</summary>
    public bool PtzPanReversed { get; set; }

    /// <summary>Tilt（Y）轴方向反转</summary>
    public bool PtzTiltReversed { get; set; }

    /// <summary>Zoom 轴方向反转</summary>
    public bool PtzZoomReversed { get; set; }

    public DateTime CreatedAt { get; set; }
}
