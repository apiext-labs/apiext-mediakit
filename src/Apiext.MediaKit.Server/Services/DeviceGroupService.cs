using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>分组内设备成员（拉流通道 / 国标通道）。</summary>
public class DeviceGroupMemberDto
{
    /// <summary>设备类型：pull | gb。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>pull: 应用名；gb: 设备编码。</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>pull: 流 ID；gb: 通道编码。</summary>
    public string Stream { get; set; } = string.Empty;
}

/// <summary>设备自定义分组。</summary>
public class DeviceGroupDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public List<DeviceGroupMemberDto> Members { get; set; } = new();
}

/// <summary>设备自定义分组：分组 CRUD + 设备归属设置（播放页设备树使用）。</summary>
public class DeviceGroupService
{
    private readonly AppDbContext _db;

    public DeviceGroupService(AppDbContext db) => _db = db;

    /// <summary>设备唯一键（分组成员匹配用），形如 pull|live|cam1。</summary>
    public static string MemberKey(string kind, string app, string stream) => $"{kind}|{app}|{stream}";

    public async Task<List<DeviceGroupDto>> ListAsync()
    {
        var groups = await _db.DeviceGroups.AsNoTracking()
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToListAsync();
        var members = await _db.DeviceGroupMembers.AsNoTracking().ToListAsync();

        return groups.Select(g => new DeviceGroupDto
        {
            Id = g.Id,
            Name = g.Name,
            SortOrder = g.SortOrder,
            Members = members.Where(m => m.GroupId == g.Id)
                .Select(m => new DeviceGroupMemberDto { Kind = m.Kind, App = m.App, Stream = m.Stream })
                .ToList(),
        }).ToList();
    }

    /// <summary>设备键 → 分组 ID 映射（仅已归属分组的设备）。</summary>
    public async Task<Dictionary<string, int>> GetMembershipMapAsync()
    {
        var members = await _db.DeviceGroupMembers.AsNoTracking().ToListAsync();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            map[MemberKey(m.Kind, m.App, m.Stream)] = m.GroupId;
        }
        return map;
    }

    public async Task<(bool Ok, string? Error, int Id)> CreateAsync(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return (false, "分组名称不能为空", 0);
        if (name.Length > 32) return (false, "分组名称不能超过 32 个字符", 0);
        if (await _db.DeviceGroups.AnyAsync(g => g.Name == name)) return (false, "分组名称已存在", 0);

        var maxOrder = await _db.DeviceGroups.MaxAsync(g => (int?)g.SortOrder) ?? 0;
        var entity = new DeviceGroup { Name = name, SortOrder = maxOrder + 1, CreatedAt = DateTime.UtcNow };
        _db.DeviceGroups.Add(entity);
        await _db.SaveChangesAsync();
        return (true, null, entity.Id);
    }

    public async Task<(bool Ok, string? Error)> RenameAsync(int id, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return (false, "分组名称不能为空");
        if (name.Length > 32) return (false, "分组名称不能超过 32 个字符");

        var group = await _db.DeviceGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return (false, "分组不存在");
        if (await _db.DeviceGroups.AnyAsync(g => g.Name == name && g.Id != id)) return (false, "分组名称已存在");

        group.Name = name;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>删除分组；组内设备回归「未分组」（成员记录一并删除）。</summary>
    public async Task<(bool Ok, string? Error)> DeleteAsync(int id)
    {
        var group = await _db.DeviceGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return (false, "分组不存在");

        var members = await _db.DeviceGroupMembers.Where(m => m.GroupId == id).ToListAsync();
        _db.DeviceGroupMembers.RemoveRange(members);
        _db.DeviceGroups.Remove(group);
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>设置设备归属分组；groupId 为 null 或 0 表示移出分组。</summary>
    public async Task<(bool Ok, string? Error)> SetMemberGroupAsync(string kind, string app, string stream, int? groupId)
    {
        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(app) || string.IsNullOrEmpty(stream))
        {
            return (false, "设备标识不完整");
        }

        var row = await _db.DeviceGroupMembers
            .FirstOrDefaultAsync(m => m.Kind == kind && m.App == app && m.Stream == stream);

        if (groupId is null or <= 0)
        {
            if (row != null)
            {
                _db.DeviceGroupMembers.Remove(row);
                await _db.SaveChangesAsync();
            }
            return (true, null);
        }

        if (!await _db.DeviceGroups.AnyAsync(g => g.Id == groupId.Value))
        {
            return (false, "分组不存在");
        }

        if (row == null)
        {
            _db.DeviceGroupMembers.Add(new DeviceGroupMember
            {
                Kind = kind,
                App = app,
                Stream = stream,
                GroupId = groupId.Value,
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            row.GroupId = groupId.Value;
        }

        await _db.SaveChangesAsync();
        return (true, null);
    }
}
