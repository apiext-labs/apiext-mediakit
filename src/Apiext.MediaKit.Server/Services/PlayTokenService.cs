using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Apiext.MediaKit.Server.Services;

/// <summary>播放令牌声明：短时效、绑定资源，ZLM 在 on_play / on_http_access 时校验。</summary>
public sealed class PlayTokenClaims
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("aud")] public string Audience { get; set; } = string.Empty;
    [JsonPropertyName("app")] public string? App { get; set; }
    [JsonPropertyName("stream")] public string? Stream { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("node")] public string Node { get; set; } = string.Empty;
    [JsonPropertyName("iat")] public long IssuedAt { get; set; }
    [JsonPropertyName("exp")] public long ExpiresAt { get; set; }
    [JsonPropertyName("nb")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("iph")] public string? IpHash { get; set; }
}

/// <summary>
/// 播放令牌签发与校验：HMAC-SHA256 签名（payload.signature，base64url），默认 30 分钟有效、
/// 过期后仍有 12 小时宽限（覆盖播放器断线重连）。令牌不落库，可直接拼在播放地址上。
/// </summary>
public class PlayTokenService
{
    public const string QueryParameter = "play_token";

    private const string Audience = "apiext-mediakit-play";
    private const string AudiencePath = "apiext-mediakit-file";
    private const int MinKeyBytes = 32;
    private const int MaxClockSkewSeconds = 30;

    private readonly PlayTokenOptions _opt;
    private readonly ZlmHookOptions _hook;
    private readonly ILogger<PlayTokenService> _logger;
    private bool _warnedDisabled;

    public PlayTokenService(
        IOptions<PlayTokenOptions> options,
        IOptions<ZlmHookOptions> hookOptions,
        ILogger<PlayTokenService> logger)
    {
        _opt = options.Value;
        _hook = hookOptions.Value;
        _logger = logger;
    }

    /// <summary>密钥不足 32 字节时功能关闭（播放地址不带令牌，on_play 放行）。</summary>
    public bool Enabled => Encoding.UTF8.GetByteCount(_opt.Secret ?? string.Empty) >= MinKeyBytes;

    /// <summary>令牌有效期（秒），管理界面展示用。</summary>
    public int TtlSeconds => Math.Max(30, _opt.TtlSeconds);

    /// <summary>过期后的宽限（秒），管理界面展示用。</summary>
    public int GraceSeconds => Math.Max(0, _opt.GraceSeconds);

    /// <summary>是否把令牌绑定到客户端 IP。</summary>
    public bool BindClientIp => _opt.BindClientIp;

    /// <summary>令牌绑定的媒体节点 ID（ZlmHook:MediaServerId）。</summary>
    public string MediaServerId => _hook.MediaServerId;

    /// <summary>为一路流签发播放令牌。</summary>
    public string? Issue(string app, string stream, string? clientIp = null)
        => IssueClaims(new PlayTokenClaims
        {
            Audience = Audience,
            App = app,
            Stream = stream,
            Node = _hook.MediaServerId,
            IpHash = _opt.BindClientIp ? ClientIpHash(clientIp) : null,
        });

    /// <summary>为单个 HTTP 文件（如录像 mp4）签发令牌。</summary>
    public string? IssueForPath(string path)
        => IssueClaims(new PlayTokenClaims
        {
            Audience = AudiencePath,
            Path = path,
            Node = _hook.MediaServerId,
        });

    /// <summary>校验流播放令牌；未启用时放行。</summary>
    public (bool Ok, string Reason) Verify(string? token, string app, string stream, string? clientIp)
    {
        if (!Enabled) return WarnDisabled();

        var claims = ReadAndVerify(token, out var error);
        if (claims is null) return (false, error);

        if (claims.Audience != Audience) return (false, "令牌用途不匹配");
        if (!string.Equals(claims.Node, _hook.MediaServerId, StringComparison.Ordinal))
            return (false, "令牌节点不匹配");
        if (!string.Equals(claims.App, app, StringComparison.Ordinal) ||
            !string.Equals(claims.Stream, stream, StringComparison.Ordinal))
            return (false, "令牌与流不匹配");

        return CheckLifetimeAndIp(claims, clientIp);
    }

    /// <summary>校验文件访问令牌；未启用时放行。</summary>
    public (bool Ok, string Reason) VerifyPath(string? token, string path)
    {
        if (!Enabled) return WarnDisabled();

        var claims = ReadAndVerify(token, out var error);
        if (claims is null) return (false, error);

        if (claims.Audience != AudiencePath) return (false, "令牌用途不匹配");
        if (!string.Equals(claims.Node, _hook.MediaServerId, StringComparison.Ordinal))
            return (false, "令牌节点不匹配");
        if (!string.Equals(claims.Path, path, StringComparison.Ordinal))
            return (false, "令牌与文件不匹配");

        return CheckLifetime(claims);
    }

    /// <summary>从 ZLM 回调的 params（URL query）里取播放令牌。</summary>
    public string? ExtractFromParams(string? rawParams)
    {
        if (string.IsNullOrEmpty(rawParams)) return null;
        var query = QueryHelpers.ParseQuery(rawParams.TrimStart('?'));
        return query.TryGetValue(QueryParameter, out var value) ? value.ToString() : null;
    }

    /// <summary>给播放地址追加 play_token；未启用时原样返回。</summary>
    public string Decorate(string url, string app, string stream)
    {
        if (!Enabled) return url;
        var token = Issue(app, stream);
        return string.IsNullOrEmpty(token) ? url : AppendQuery(url, token);
    }

    /// <summary>给文件地址追加 play_token（路径级绑定）；未启用时原样返回。</summary>
    public string DecoratePath(string url, string path)
    {
        if (!Enabled) return url;
        var token = IssueForPath(path);
        return string.IsNullOrEmpty(token) ? url : AppendQuery(url, token);
    }

    // ---------- 内部实现 ----------

    private string? IssueClaims(PlayTokenClaims claims)
    {
        if (!Enabled) return null;

        var now = DateTimeOffset.UtcNow;
        claims.Version = 1;
        claims.IssuedAt = now.ToUnixTimeSeconds();
        claims.ExpiresAt = now.AddSeconds(Math.Max(30, _opt.TtlSeconds)).ToUnixTimeSeconds();
        claims.Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var payload = JsonSerializer.SerializeToUtf8Bytes(claims);
        var encoded = Base64UrlEncode(payload);
        var signature = Sign(Encoding.ASCII.GetBytes(encoded));
        return $"{encoded}.{Base64UrlEncode(signature)}";
    }

    private PlayTokenClaims? ReadAndVerify(string? token, out string error)
    {
        error = "缺少播放令牌";
        if (string.IsNullOrEmpty(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            error = "播放令牌格式错误";
            return null;
        }

        byte[] provided;
        try
        {
            provided = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            error = "播放令牌格式错误";
            return null;
        }

        var expected = Sign(Encoding.ASCII.GetBytes(parts[0]));
        if (provided.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            error = "播放令牌签名无效";
            return null;
        }

        PlayTokenClaims? claims;
        try
        {
            claims = JsonSerializer.Deserialize<PlayTokenClaims>(Base64UrlDecode(parts[0]));
        }
        catch (JsonException)
        {
            error = "播放令牌内容无效";
            return null;
        }

        if (claims is null || claims.Version != 1)
        {
            error = "播放令牌版本不支持";
            return null;
        }

        error = string.Empty;
        return claims;
    }

    private (bool Ok, string Reason) CheckLifetimeAndIp(PlayTokenClaims claims, string? clientIp)
    {
        var (ok, reason) = CheckLifetime(claims);
        if (!ok) return (false, reason);

        if (!string.IsNullOrEmpty(claims.IpHash))
        {
            var actual = ClientIpHash(clientIp);
            if (actual is null || !string.Equals(actual, claims.IpHash, StringComparison.Ordinal))
                return (false, "客户端地址不匹配");
        }

        return (true, string.Empty);
    }

    private (bool Ok, string Reason) CheckLifetime(PlayTokenClaims claims)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (claims.IssuedAt <= 0 || claims.ExpiresAt <= claims.IssuedAt)
            return (false, "播放令牌时间无效");
        if (claims.IssuedAt > now + MaxClockSkewSeconds)
            return (false, "播放令牌签发时间异常");
        if (now > claims.ExpiresAt + Math.Max(0, _opt.GraceSeconds))
            return (false, "播放令牌已过期");
        return (true, string.Empty);
    }

    private (bool Ok, string Reason) WarnDisabled()
    {
        if (!_warnedDisabled)
        {
            _warnedDisabled = true;
            _logger.LogWarning("[PlayToken] 未配置 PlayToken:Secret（需 ≥32 字节），播放鉴权已关闭");
        }
        return (true, string.Empty);
    }

    private byte[] Sign(byte[] payload)
        => HMACSHA256.HashData(Encoding.UTF8.GetBytes(_opt.Secret), payload);

    private string? ClientIpHash(string? rawIp)
    {
        if (string.IsNullOrWhiteSpace(rawIp)) return null;
        var value = rawIp.Trim();
        if (IPEndPoint.TryParse(value, out var endpoint)) value = endpoint.Address.ToString();
        if (value.StartsWith('[') && value.Contains(']'))
            value = value.Trim('[', ']');
        if (!IPAddress.TryParse(value, out var address)) return null;

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
        var digest = Sign(Encoding.UTF8.GetBytes(normalized));
        return Base64UrlEncode(digest, 9);
    }

    private static string AppendQuery(string url, string token)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{QueryParameter}={Uri.EscapeDataString(token)}";
    }

    internal static string Base64UrlEncode(byte[] bytes, int take = 0)
    {
        if (take > 0 && take < bytes.Length) bytes = bytes[..take];
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }
}
