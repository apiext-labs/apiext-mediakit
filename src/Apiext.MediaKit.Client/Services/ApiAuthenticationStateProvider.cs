using System.Net.Http.Json;
using System.Security.Claims;
using Apiext.MediaKit.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace Apiext.MediaKit.Services;

/// <summary>
/// WASM-side auth state. Host overrides AuthenticationStateProvider with HostCookieAuthenticationStateProvider.
/// </summary>
public sealed class ApiAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());
    private readonly HttpClient _httpClient;
    private readonly IAmbientUserAccessor _ambientUser;

    public ApiAuthenticationStateProvider(HttpClient httpClient, IAmbientUserAccessor ambientUser)
    {
        _httpClient = httpClient;
        _ambientUser = ambientUser;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var ambient = _ambientUser.User;
        if (ambient?.Identity?.IsAuthenticated == true)
            return new AuthenticationState(ambient);

        try
        {
            using var response = await _httpClient.GetAsync("api/auth/me");
            if (!response.IsSuccessStatusCode)
                return new AuthenticationState(Anonymous);

            var user = await response.Content.ReadFromJsonAsync<AuthUserDto>();
            return new AuthenticationState(user is { IsAuthenticated: true } ? CreatePrincipal(user) : Anonymous);
        }
        catch (Exception)
        {
            return new AuthenticationState(Anonymous);
        }
    }

    private static ClaimsPrincipal CreatePrincipal(AuthUserDto user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.UserName),
        new Claim(ClaimTypes.Name, user.DisplayName),
        new Claim(ClaimTypes.Role, user.Role)
    ], authenticationType: "Cookie"));
}
