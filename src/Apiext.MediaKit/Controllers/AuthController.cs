using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Apiext.MediaKit.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Controllers;

/// <summary>
/// Single-admin cookie auth. No registration, no multi-user, no social login.
/// Browser form POST is the supported login path (Set-Cookie must hit the real browser).
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    public AuthController(IConfiguration configuration) => _configuration = configuration;

    /// <summary>Browser form login — primary path for Interactive Server / SSR.</summary>
    [AllowAnonymous]
    [HttpPost("login-form")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> LoginForm(
        [FromForm] string userName,
        [FromForm] string password,
        [FromForm] bool autoLogin = false,
        [FromForm] string? returnUrl = null)
    {
        if (!TryAuthenticate(userName, password, out var name, out var displayName))
        {
            return Redirect(
                $"/user/login?error={Uri.EscapeDataString("用户名或密码错误")}&returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}");
        }

        await SignInAsync(name, displayName, autoLogin);
        return LocalRedirect(SafeReturnUrl(returnUrl));
    }

    [Authorize]
    [HttpGet("me")]
    public ActionResult<AuthUserDto> Me() => Ok(new AuthUserDto(
        true,
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
        User.Identity?.Name ?? string.Empty,
        User.FindFirstValue(ClaimTypes.Role) ?? string.Empty));

    /// <summary>Browser navigation logout so Set-Cookie clear lands on the client.</summary>
    [AllowAnonymous]
    [HttpGet("logout")]
    public async Task<IActionResult> LogoutGet()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/user/login");
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutPost()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    private bool TryAuthenticate(string? userName, string? password, out string name, out string displayName)
    {
        name = _configuration["Authentication:UserName"] ?? "admin";
        displayName = _configuration["Authentication:DisplayName"] ?? name;
        var expectedPassword = _configuration["Authentication:Password"] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(userName)
            && !string.IsNullOrWhiteSpace(password)
            && FixedEquals(userName.Trim(), name)
            && FixedEquals(password, expectedPassword);
    }

    private async Task SignInAsync(string userName, string displayName, bool rememberMe)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userName),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Role, "Administrator")
        };
        var properties = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(14) : null
        };
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            properties);
    }

    private static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal)) return "/";
        if (returnUrl.StartsWith("/user/login", StringComparison.OrdinalIgnoreCase)) return "/";
        if (returnUrl.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return "/";
        return returnUrl;
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left ?? string.Empty));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
