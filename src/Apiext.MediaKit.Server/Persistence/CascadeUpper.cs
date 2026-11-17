namespace Apiext.MediaKit.Server.Persistence;

/// <summary>GB28181 平台级联的上级平台配置（持久化，可动态增删改）。</summary>
public class CascadeUpper
{
    public int Id { get; set; }

    /// <summary>上级平台名称（展示用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>上级平台 SIP 地址。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>上级平台 SIP 端口。</summary>
    public int Port { get; set; } = 5060;

    /// <summary>上级平台编码（国标 20 位）。</summary>
    public string PlatformId { get; set; } = string.Empty;

    /// <summary>上级平台域。</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>上级鉴权 realm（可空，优先用 401 返回的 realm）。</summary>
    public string Realm { get; set; } = string.Empty;

    /// <summary>向上级注册的鉴权密码。</summary>
    public string Password { get; set; } = "admin123";

    public DateTime CreatedAt { get; set; }

    /// <summary>运行时键：IP:端口，作为唯一标识。</summary>
    public string Key => $"{Ip}:{Port}";
}
