namespace Apiext.MediaKit.Models;

/// <summary>Header avatar model — real auth identity, not demo profile data.</summary>
public sealed class CurrentUser
{
    public string Name { get; set; } = "未登录";
    public string Avatar { get; set; } = "assets/logo.svg";
    public string Userid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}
