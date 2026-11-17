namespace Apiext.MediaKit.Server.Persistence;

/// <summary>预登记的国标设备（白名单鉴权模式使用）：设备注册时按国标编码匹配并校验独立密码。</summary>
public class GbPresetDevice
{
    public int Id { get; set; }

    /// <summary>设备国标编码（20 位），作为 SIP 注册的用户名。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称（展示用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>设备独立接入密码（留空则回退到全局 Gb28181:Password）。</summary>
    public string Password { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
