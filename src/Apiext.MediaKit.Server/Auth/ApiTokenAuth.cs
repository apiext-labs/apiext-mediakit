namespace Apiext.MediaKit.Server.Auth;

/// <summary>
/// 访问令牌的提取与掩码工具。
/// 鉴权本身由 <see cref="ApiTokenAuthAttribute"/>（Controller/Action 特性）承担；
/// 这里保留静态方法供播放代理在改写 m3u8 时原样透传 token。
/// </summary>
public static class ApiTokenAuth
{
    /// <summary>提取令牌：优先 ?token=，其次 Authorization: Bearer。</summary>
    public static string? ExtractToken(HttpContext http)
    {
        if (http.Request.Query.TryGetValue("token", out var q) && !string.IsNullOrEmpty(q.ToString()))
            return q.ToString();

        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth[7..].Trim();

        return null;
    }

    /// <summary>令牌掩码：只保留前 6 位，避免把可用凭据写进日志。</summary>
    public static string Mask(string token)
        => token.Length <= 8 ? "***" : token[..6] + "…";
}
