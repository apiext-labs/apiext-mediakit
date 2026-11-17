using Apiext.MediaKit.Server.Auth;
using Apiext.MediaKit.Server.Contracts;
using Apiext.MediaKit.Server.Onvif;
using Apiext.MediaKit.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Server.Controllers;

/// <summary>
/// ONVIF 对外 REST API（/api/onvif）：云台控制、预置位。
/// 统一走访问令牌鉴权。
/// </summary>
[ApiController]
[Route("api/onvif")]
[ApiTokenAuth]
public sealed class OnvifController : ControllerBase
{
    private readonly OnvifService _onvif;

    public OnvifController(OnvifService onvif) => _onvif = onvif;

    /// <summary>云台控制（固定地址 + action 参数变化）。</summary>
    [HttpPost("devices/{id:int}/ptz")]
    public async Task<IActionResult> Ptz(int id, [FromBody] OnvifPtzRequest req)
    {
        switch (req.Action?.ToLowerInvariant())
        {
            case "stop":
            {
                var (ok, error) = await _onvif.PtzStopAsync(id);
                return ok ? Ok(new { success = true }) : BadRequest(new { success = false, error });
            }
            case "move":
            {
                if (!Enum.TryParse<PtzDirection>(req.Direction, true, out var dir))
                    return BadRequest(new { success = false, error = "无效的方向（Up/Down/Left/Right/ZoomIn/ZoomOut）" });

                var (ok, error) = await _onvif.PtzMoveAsync(id, dir, Math.Clamp(req.Speed, 0.1, 1.0));
                return ok ? Ok(new { success = true }) : BadRequest(new { success = false, error });
            }
            case "savepreset":
            {
                if (string.IsNullOrWhiteSpace(req.PresetName))
                    return BadRequest(new { success = false, error = "缺少预置位名称" });

                var (ok, error) = await _onvif.SavePresetAsync(id, req.PresetName.Trim());
                return ok ? Ok(new { success = true }) : BadRequest(new { success = false, error });
            }
            case "gotopreset":
            {
                if (string.IsNullOrWhiteSpace(req.PresetToken))
                    return BadRequest(new { success = false, error = "缺少预置位 token" });

                var (ok, error) = await _onvif.GotoPresetAsync(id, req.PresetToken);
                return ok ? Ok(new { success = true }) : BadRequest(new { success = false, error });
            }
            case "removepreset":
            {
                if (string.IsNullOrWhiteSpace(req.PresetToken))
                    return BadRequest(new { success = false, error = "缺少预置位 token" });

                var (ok, error) = await _onvif.RemovePresetAsync(id, req.PresetToken);
                return ok ? Ok(new { success = true }) : BadRequest(new { success = false, error });
            }
            default:
                return BadRequest(new { success = false, error = "无效的 action（move/stop/savePreset/gotoPreset/removePreset）" });
        }
    }

    /// <summary>预置位列表。</summary>
    [HttpGet("devices/{id:int}/presets")]
    public async Task<IActionResult> Presets(int id)
    {
        var (ok, error, presets) = await _onvif.GetPresetsAsync(id);
        return ok ? Ok(presets) : BadRequest(new { success = false, error });
    }
}
