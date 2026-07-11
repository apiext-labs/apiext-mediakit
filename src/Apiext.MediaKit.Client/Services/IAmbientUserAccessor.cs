using System.Security.Claims;

namespace Apiext.MediaKit.Services;

/// <summary>
/// Optional ambient principal. Host (SSR / Interactive Server) supplies HttpContext.User;
/// pure WASM keeps the no-op implementation so the Client project stays framework-light.
/// </summary>
public interface IAmbientUserAccessor
{
    ClaimsPrincipal? User { get; }
}

public sealed class NullAmbientUserAccessor : IAmbientUserAccessor
{
    public ClaimsPrincipal? User => null;
}
