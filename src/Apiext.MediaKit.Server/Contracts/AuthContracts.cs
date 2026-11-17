namespace Apiext.MediaKit.Server.Contracts;

/// <summary>登录请求体。</summary>
public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>修改密码请求体。</summary>
public sealed class ChangePasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}
