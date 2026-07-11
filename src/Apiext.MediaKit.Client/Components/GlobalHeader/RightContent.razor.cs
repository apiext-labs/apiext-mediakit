using AntDesign.ProLayout;
using Apiext.MediaKit.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Apiext.MediaKit.Components;

public partial class RightContent
{
    private CurrentUser _currentUser = new()
    {
        Name = "未登录",
        Avatar = "assets/logo.svg"
    };

    private readonly AvatarMenuItem[] AvatarMenuItems =
    [
        new() { Key = "servers", IconType = "cluster", Option = "媒体实例" },
        new() { IsDivider = true },
        new() { Key = "logout", IconType = "logout", Option = "退出登录" }
    ];

    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        // Do NOT NavigateTo login here — AuthorizeRouteView/RedirectToLogin owns that.
        var state = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var principal = state.User;
        if (principal.Identity?.IsAuthenticated != true)
            return;

        _currentUser = new CurrentUser
        {
            Userid = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty,
            Name = principal.Identity.Name
                ?? principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                ?? "管理员",
            Title = principal.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? string.Empty,
            Avatar = "assets/logo.svg"
        };
    }

    private void HandleSelectUser(MenuItem item)
    {
        if (item.Key == "servers")
        {
            Navigation.NavigateTo("/servers");
            return;
        }

        if (item.Key != "logout") return;
        Navigation.NavigateTo("/api/auth/logout", forceLoad: true);
    }
}
