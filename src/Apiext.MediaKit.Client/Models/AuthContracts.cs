namespace Apiext.MediaKit.Models;

/// <summary>Authenticated admin identity returned by GET /api/auth/me.</summary>
public sealed record AuthUserDto(bool IsAuthenticated, string UserName, string DisplayName, string Role);
