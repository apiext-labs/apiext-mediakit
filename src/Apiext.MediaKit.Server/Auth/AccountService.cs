using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Auth;

/// <summary>管理员账号维护（修改密码）。修改后无需重新登录（Cookie/JWT 以用户名主体签发）。</summary>
public class AccountService
{
    private readonly AppDbContext _db;

    public AccountService(AppDbContext db) => _db = db;

    /// <summary>修改指定用户密码（PBKDF2 重新散列）。</summary>
    public async Task<(bool Ok, string? Error)> ChangePasswordAsync(string username, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            return (false, "新密码长度至少 6 位");

        var user = await _db.AdminUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return (false, "用户不存在");

        var (hash, salt) = PasswordHasher.Hash(newPassword);
        user.PasswordHash = hash;
        user.Salt = salt;
        await _db.SaveChangesAsync();
        return (true, null);
    }
}
