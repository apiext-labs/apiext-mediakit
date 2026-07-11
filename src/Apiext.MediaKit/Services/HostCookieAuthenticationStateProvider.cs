using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Apiext.MediaKit.HostServices;

/// <summary>
/// Host auth state from the ASP.NET Core cookie principal (SSR / Interactive Server).
/// </summary>
public sealed class HostCookieAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HostCookieAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var user = _httpContextAccessor.HttpContext?.User
            ?? new ClaimsPrincipal(new ClaimsIdentity());
        return Task.FromResult(new AuthenticationState(user));
    }
}
