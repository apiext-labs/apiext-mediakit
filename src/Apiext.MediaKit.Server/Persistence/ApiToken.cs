namespace Apiext.MediaKit.Server.Persistence;

/// <summary>
/// 全局访问令牌：保护对外暴露的播放地址与云台控制 API。
/// 令牌明文存储（便于拼接播放地址 URL），可单独启用/禁用、设置有效期。
/// </summary>
public class ApiToken
{
    public int Id { get; set; }

    /// <summary>令牌名称（备注）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>令牌明文（唯一）。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>过期时间；null 表示永久有效。</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>是否启用（禁用即失效）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>最近一次校验通过时间。</summary>
    public DateTime? LastUsedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
