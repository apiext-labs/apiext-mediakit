namespace Apiext.MediaKit.Server.Contracts;

/// <summary>ONVIF 云台控制请求体：固定地址 + action 参数变化。</summary>
public sealed class OnvifPtzRequest
{
    /// <summary>操作：move | stop | savePreset | gotoPreset | removePreset。</summary>
    public string Action { get; set; } = "move";

    /// <summary>move 时：Up / Down / Left / Right / ZoomIn / ZoomOut。</summary>
    public string? Direction { get; set; }

    /// <summary>move 时：速度 0.1 ~ 1.0。</summary>
    public double Speed { get; set; } = 0.5;

    /// <summary>savePreset 时：预置位名称。</summary>
    public string? PresetName { get; set; }

    /// <summary>gotoPreset / removePreset 时：预置位 token。</summary>
    public string? PresetToken { get; set; }
}
