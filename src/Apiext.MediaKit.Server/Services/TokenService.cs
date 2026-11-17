using System.Security.Cryptography;
using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 全局访问令牌服务：生成、管理、校验。令牌明文存储（便于拼接播放地址 URL）。
/// </summary>
public class TokenService
{
    private readonly AppDbContext _db;

    public TokenService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>生成新令牌（明文在返回实体的 Token 字段）。expiresAt 为 null 表示永久。</summary>
    public async Task<ApiToken> CreateAsync(string name, DateTime? expiresAt)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var entity = new ApiToken
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"令牌 {DateTime.Now:yyyyMMdd-HHmmss}" : name.Trim(),
            Token = token,
            ExpiresAt = expiresAt,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ApiTokens.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }

    /// <summary>令牌列表（管理界面展示用，含明文）。</summary>
    public Task<List<ApiToken>> ListAsync()
        => _db.ApiTokens.OrderByDescending(t => t.Id).ToListAsync();

    /// <summary>启用 / 禁用（禁用即失效）。</summary>
    public async Task<(bool Ok, string? Error)> SetEnabledAsync(int id, bool enabled)
    {
        var entity = await _db.ApiTokens.FindAsync(id);
        if (entity == null) return (false, "令牌不存在");
        entity.Enabled = enabled;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>删除令牌。</summary>
    public async Task DeleteAsync(int id)
    {
        var entity = await _db.ApiTokens.FindAsync(id);
        if (entity != null)
        {
            _db.ApiTokens.Remove(entity);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>校验令牌（存在 + 启用 + 未过期），并更新最近使用时间。通过返回实体，否则 null。</summary>
    public ApiToken? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var now = DateTime.UtcNow;
        var entity = _db.ApiTokens.FirstOrDefault(t => t.Token == token);
        if (entity == null || !entity.Enabled) return null;
        if (entity.ExpiresAt != null && entity.ExpiresAt <= now) return null;

        // 节流更新：距上次记录超过 1 分钟才写库，降低热路径写放大
        if (entity.LastUsedAt == null || (now - entity.LastUsedAt.Value).TotalMinutes >= 1)
        {
            entity.LastUsedAt = now;
            _db.SaveChanges();
        }
        return entity;
    }

    /// <summary>返回当前启用的最新令牌明文（用于拼接播放地址）；无有效令牌时返回 null。</summary>
    public string? GetActiveToken()
    {
        var now = DateTime.UtcNow;
        return _db.ApiTokens
            .Where(t => t.Enabled && (t.ExpiresAt == null || t.ExpiresAt > now))
            .OrderByDescending(t => t.Id)
            .Select(t => t.Token)
            .FirstOrDefault();
    }
}
