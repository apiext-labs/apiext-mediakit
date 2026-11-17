namespace Apiext.MediaKit.Server.Persistence;

/// <summary>GB28181 目录查询得到的通道（每个摄像头一条）。</summary>
public class GbChannel
{
    public int Id { get; set; }

    /// <summary>通道国标编码（20 位）</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>所属设备国标编码</summary>
    public string DeviceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string CivilCode { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;

    /// <summary>是否父节点（0 通道 / 1 组织目录节点）</summary>
    public int Parental { get; set; }

    public string ParentId { get; set; } = string.Empty;

    /// <summary>通道状态 ON/OFF</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>所属设备（按国标编码关联）。</summary>
    public GbDevice Device { get; set; } = null!;
}
