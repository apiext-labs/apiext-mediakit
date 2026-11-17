using Apiext.MediaKit.Server.Persistence;
using Apiext.MediaKit.Server.Services;
using Microsoft.EntityFrameworkCore;
using SIPSorcery.SIP;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>GB28181 接入判定与 Digest 校验（auto 注册即接入 / whitelist 预登记白名单）。</summary>
public class GbAuthenticator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _enableAuth;
    private readonly string _password;

    public GbAuthenticator(IServiceScopeFactory scopeFactory, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        var section = config.GetSection("Gb28181");
        _enableAuth = section.GetValue("EnableAuth", true);

        // 未配置密码属于部署错误，直接启动失败（不再静默回落到弱密码）
        var password = section.GetValue<string?>("Password");
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "缺少配置 Gb28181:Password（设备统一接入密码），请在 appsettings.json 或部署覆盖层中配置。");
        }
        _password = password;
    }

    /// <summary>
    /// 接入判定：
    /// - auto（注册即接入）：Allowed=true，是否鉴权由全局 EnableAuth 决定，密码用全局 Password；
    /// - whitelist（预登记 + 白名单鉴权）：未预登记则 Allowed=false，已预登记则总是鉴权（独立密码，留空回退全局）。
    /// </summary>
    public async Task<(bool Allowed, bool RequireAuth, string Password, string? PresetName)> CheckAccessAsync(string deviceId)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var settings = sp.GetRequiredService<AppSettingsService>();

        var mode = await settings.GetAsync("gb28181.access.mode");
        if (mode != "whitelist")
        {
            return (true, _enableAuth, _password, null);
        }

        var preset = await db.GbPresetDevices.FirstOrDefaultAsync(p => p.DeviceId == deviceId);
        if (preset == null)
        {
            return (false, false, string.Empty, null);
        }

        var pwd = string.IsNullOrEmpty(preset.Password) ? _password : preset.Password;
        return (true, true, pwd, preset.Name);
    }

    /// <summary>校验请求携带的 Digest response 是否与给定密码匹配（不发送任何响应）。</summary>
    public static bool ValidateDigest(SIPRequest request, string password)
    {
        if (request.Header.AuthenticationHeaders == null || request.Header.AuthenticationHeaders.Count == 0)
        {
            return false;
        }

        var digest = request.Header.AuthenticationHeaders[0].SIPDigest;
        var expected = GbDigest.ComputeResponse(digest.Username, digest.Realm, password, "REGISTER", digest.URI, digest.Nonce);
        return string.Equals(expected, digest.Response, StringComparison.OrdinalIgnoreCase);
    }
}
