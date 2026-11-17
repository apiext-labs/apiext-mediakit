namespace Apiext.MediaKit.Server.Contracts;

/// <summary>
/// 新增拉流代理请求体。
/// App / Stream / Url 声明为可空：必填校验放在控制器里做，
/// 避免 <c>[ApiController]</c> 的隐式 required 校验把提示换成 ProblemDetails。
/// </summary>
public sealed record CreatePullProxyRequest(
    string? App = null,
    string? Stream = null,
    string? Url = null,
    string? Vhost = null,
    int? AudioType = null,
    bool EnableOnDemand = false,
    bool EnableRecord = false,
    string? PullMode = "native",
    string? OutputCodec = "copy");

/// <summary>更新拉流代理请求体（先删后加）。</summary>
public sealed record UpdatePullProxyRequest(
    string? Url = null,
    string? Vhost = null,
    int? AudioType = null,
    string? OutputCodec = "copy",
    string? PullMode = null);
