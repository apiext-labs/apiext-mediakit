using Apiext.MediaKit.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Apiext.MediaKit.Server.Auth;

/// <summary>
/// 访问令牌鉴权（Controller 版）：贴在 Controller 或 Action 上即可要求
/// <c>?token=</c> 或 <c>Authorization: Bearer &lt;访问令牌&gt;</c>。
/// 失败统一返回 401 + <c>{ success:false, error }</c>，并留痕（区分「没带」与「无效」）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ApiTokenAuthAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var tokens = http.RequestServices.GetRequiredService<TokenService>();
        var raw = ApiTokenAuth.ExtractToken(http);

        if (tokens.Validate(raw) != null) return Task.CompletedTask;

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Apiext.MediaKit.Server.Auth.ApiTokenAuth");

        if (string.IsNullOrEmpty(raw))
        {
            logger.LogWarning("[Token] 缺少访问令牌 {Method} {Path} 来源={Ip}",
                http.Request.Method, http.Request.Path, http.Connection.RemoteIpAddress?.ToString() ?? "-");
        }
        else
        {
            logger.LogWarning("[Token] 访问令牌无效/已禁用/已过期 {Method} {Path} 来源={Ip} token={Masked}",
                http.Request.Method, http.Request.Path, http.Connection.RemoteIpAddress?.ToString() ?? "-",
                ApiTokenAuth.Mask(raw));
        }

        context.Result = new JsonResult(new { success = false, error = "无效或过期的访问令牌" })
        {
            StatusCode = StatusCodes.Status401Unauthorized,
        };
        return Task.CompletedTask;
    }
}
