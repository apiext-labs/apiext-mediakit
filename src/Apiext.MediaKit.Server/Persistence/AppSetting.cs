namespace Apiext.MediaKit.Server.Persistence;

/// <summary>系统级键值设置（持久化），如初始化向导完成标记。</summary>
public class AppSetting
{
    public int Id { get; set; }

    /// <summary>设置键（唯一）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>设置值。</summary>
    public string Value { get; set; } = string.Empty;
}
