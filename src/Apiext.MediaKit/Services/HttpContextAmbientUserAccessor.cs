using System.Security.Claims;
using Apiext.MediaKit.Services;

namespace Apiext.MediaKit.HostServices;

/// <summary>
/// Reads the authenticated cookie principal from the current HTTP request (SSR / Interactive Server).
/// </summary>
public sealed class HttpContextAmbientUserAccessor : IAmbientUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextAmbientUserAccessor(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    public ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;
}
