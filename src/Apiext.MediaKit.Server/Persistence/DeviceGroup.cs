namespace Apiext.MediaKit.Server.Persistence;

/// <summary>设备自定义分组（播放页设备树按此分组展示）</summary>
public class DeviceGroup
{
    public int Id { get; set; }

    /// <summary>分组名称（唯一）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>排序权重，升序展示</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>分组与设备成员关联：一个设备（拉流通道 / 国标通道）最多归属一个分组</summary>
public class DeviceGroupMember
{
    public int Id { get; set; }

    /// <summary>设备类型：pull（拉流通道）| gb（国标通道）</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>pull: 应用名；gb: 设备编码</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>pull: 流 ID；gb: 通道编码</summary>
    public string Stream { get; set; } = string.Empty;

    public int GroupId { get; set; }

    public DateTime CreatedAt { get; set; }
}
