using System.Security.Claims;
using Apiext.MediaKit.Server.Auth;
using Apiext.MediaKit.Server.Contracts;
using Apiext.MediaKit.Server.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Server.Controllers;

/// <summary>
/// 认证接口（/api/auth）：登录（写 Cookie + 签发 JWT）、登出、修改密码。
/// 登录/登出必须由浏览器 JS 发起（wwwroot/js/auth.js），否则 Cookie 到不了浏览器。
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly AccountService _accounts;

    public AuthController(AppDbContext db, JwtTokenService jwt, AccountService accounts)
    {
        _db = db;
        _jwt = jwt;
        _accounts = accounts;
    }

    /// <summary>登录：校验账号后写页面 Cookie，并返回 API 用的 JWT。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest model)
    {
        var user = _db.AdminUsers.FirstOrDefault(u => u.Username == model.Username);
        if (user is null || !PasswordHasher.Verify(model.Password, user.Salt, user.PasswordHash))
            return Unauthorized(new { success = false, error = "用户名或密码错误" });

        // 写认证 Cookie（供 Blazor 页面授权）
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, "admin"),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        // 签发 JWT（供后续 API 调用）
        var token = _jwt.CreateToken(user.Username);
        return Ok(new { success = true, token });
    }

    /// <summary>登出：清 Cookie（JWT 无状态，由调用方自行丢弃）。</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { success = true });
    }

    /// <summary>修改当前登录用户密码（Cookie / JWT 均可）。</summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest model)
    {
        var identity = HttpContext.User.Identity;
        if (identity?.IsAuthenticated != true || string.IsNullOrEmpty(identity.Name))
            return Unauthorized(new { success = false, error = "未登录" });

        var (ok, error) = await _accounts.ChangePasswordAsync(identity.Name, model.NewPassword);
        return ok
            ? Ok(new { success = true })
            : BadRequest(new { success = false, error });
    }
}
