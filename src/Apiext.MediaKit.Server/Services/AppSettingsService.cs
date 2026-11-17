using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>系统级键值设置（持久化于 DB）：读取/写入，供初始化向导标记等功能使用。</summary>
public class AppSettingsService
{
    private readonly AppDbContext _db;

    public AppSettingsService(AppDbContext db) => _db = db;

    public async Task<string?> GetAsync(string key)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        return row?.Value;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false)
    {
        var value = await GetAsync(key);
        return value != null && bool.TryParse(value, out var b) && b;
    }

    public async Task SetAsync(string key, string value)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row == null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
        await _db.SaveChangesAsync();
    }
}
