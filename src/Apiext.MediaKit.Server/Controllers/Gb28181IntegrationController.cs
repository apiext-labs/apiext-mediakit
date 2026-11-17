using Apiext.MediaKit.Server.Auth;
using Apiext.MediaKit.Server.Contracts;
using Apiext.MediaKit.Server.Gb28181;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Server.Controllers;

/// <summary>
/// GB28181 「对外集成」接口（/api/gb28181/channels/{channelId}）：云台控制、校时。
/// 这类接口面向第三方系统，用访问令牌鉴权（不要求 JWT），与 <see cref="Gb28181Controller"/> 同一路径前缀但模板互斥。
/// </summary>
[ApiController]
[Route("api/gb28181/channels/{channelId}")]
[ApiTokenAuth]
public sealed class Gb28181IntegrationController : ControllerBase
{
    private readonly Gb28181Service _gb;

    public Gb28181IntegrationController(Gb28181Service gb) => _gb = gb;

    /// <summary>云台控制（body 为 { command, param }）。</summary>
    [HttpPost("ptz")]
    public async Task<IActionResult> Ptz(string channelId, [FromBody] GbPtzRequest req)
    {
        if (!Enum.TryParse<PtzCommand>(req.Command, true, out var cmd))
            return BadRequest(new { success = false, error = "无效的云台命令" });

        await _gb.PtzControlAsync(channelId, cmd, req.Param);
        return Ok(new { success = true });
    }

    /// <summary>设备校时。</summary>
    [HttpPost("timesync")]
    public async Task<IActionResult> TimeSync(string channelId)
    {
        await _gb.TimeSyncAsync(channelId);
        return Ok(new { success = true });
    }
}
