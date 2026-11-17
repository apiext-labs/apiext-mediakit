using Microsoft.JSInterop;

namespace Apiext.MediaKit.Server.Auth;

public record LoginResult(bool Success, string? Token, string? Error);

public class AuthService
{
    private readonly IJSRuntime _js;

    public AuthService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        var result = await _js.InvokeAsync<string>("apiextAuth.login", username, password);
        if (result.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            return new LoginResult(false, null, result[6..]);
        }
        return new LoginResult(true, result, null);
    }

    public async Task LogoutAsync()
    {
        await _js.InvokeVoidAsync("apiextAuth.logout");
    }
}
