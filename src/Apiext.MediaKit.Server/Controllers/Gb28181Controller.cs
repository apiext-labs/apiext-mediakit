using Apiext.MediaKit.Server.Contracts;
using Apiext.MediaKit.Server.Gb28181;
using Apiext.MediaKit.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Server.Controllers;

/// <summary>
/// GB28181 REST API（/api/gb28181，JWT Bearer 鉴权）：
/// 设备/通道、实时点播、录像查询/回放、报警订阅、平台级联。
/// 云台/校时这类「对外集成」接口用访问令牌鉴权，见 <see cref="Gb28181IntegrationController"/>。
/// </summary>
[ApiController]
[Route("api/gb28181")]
[Authorize(Policy = "Bearer")]
public sealed class Gb28181Controller : ControllerBase
{
    private readonly Gb28181Service _gb;
    private readonly GbCascadeEngine _cascade;

    public Gb28181Controller(Gb28181Service gb, GbCascadeEngine cascade)
    {
        _gb = gb;
        _cascade = cascade;
    }

    // ---------- 设备与通道 ----------

    /// <summary>设备列表。</summary>
    [HttpGet("devices")]
    public async Task<IActionResult> Devices()
        => Ok(await _gb.GetDevicesAsync());

    /// <summary>通道列表。</summary>
    [HttpGet("devices/{deviceId}/channels")]
    public async Task<IActionResult> Channels(string deviceId)
        => Ok(await _gb.GetChannelsAsync(deviceId));

    /// <summary>触发目录查询。</summary>
    [HttpPost("devices/{deviceId}/catalog")]
    public async Task<IActionResult> Catalog(string deviceId)
    {
        var (ok, error) = await _gb.QueryCatalogAsync(deviceId);
        return ok ? Ok(new { success = true }) : BadRequest(new { success = false, error });
    }

    // ---------- 实时点播 ----------

    /// <summary>实时点播（返回播放地址）。</summary>
    [HttpPost("channels/{channelId}/play")]
    public async Task<IActionResult> Play(string channelId)
    {
        var (ok, error, url) = await _gb.StartPlayAsync(channelId);
        return ok
            ? Ok(new { success = true, url, urls = _gb.GetPlayUrls(channelId) })
            : BadRequest(new { success = false, error });
    }

    /// <summary>停止点播。</summary>
    [HttpDelete("channels/{channelId}/play")]
    public async Task<IActionResult> StopPlay(string channelId)
    {
        await _gb.StopPlayAsync(channelId);
        return Ok(new { success = true });
    }

    // ---------- 录像查询 / 回放 ----------

    /// <summary>录像查询。</summary>
    [HttpGet("channels/{channelId}/records")]
    public async Task<IActionResult> Records(string channelId, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var (ok, error, records) = await _gb.RecordQueryAsync(
            channelId, start ?? DateTime.Now.AddDays(-1), end ?? DateTime.Now);
        return ok ? Ok(records) : BadRequest(new { success = false, error });
    }

    /// <summary>回放点播（body 为 GbRecordItem）。</summary>
    [HttpPost("channels/{channelId}/playback")]
    public async Task<IActionResult> Playback(string channelId, [FromBody] GbRecordItem record)
    {
        var (ok, error, url, streamId) = await _gb.StartPlaybackAsync(channelId, record);
        return ok ? Ok(new { success = true, url, streamId }) : BadRequest(new { success = false, error });
    }

    /// <summary>回放控制（暂停/拖动）。</summary>
    [HttpPost("playback/{streamId}/control")]
    public async Task<IActionResult> PlaybackControl(string streamId, [FromBody] GbPlaybackControlRequest req)
    {
        await _gb.PlaybackControlAsync(streamId, req.RangeTime, req.Pause);
        return Ok(new { success = true });
    }

    /// <summary>停止回放。</summary>
    [HttpDelete("playback/{streamId}")]
    public async Task<IActionResult> StopPlayback(string streamId)
    {
        await _gb.StopPlaybackAsync(streamId);
        return Ok(new { success = true });
    }

    // ---------- 报警 ----------

    /// <summary>报警列表。</summary>
    [HttpGet("alarms")]
    public async Task<IActionResult> Alarms([FromQuery] int? limit)
        => Ok(await _gb.GetAlarmsAsync(limit ?? 100));

    /// <summary>订阅通道报警。</summary>
    [HttpPost("channels/{channelId}/alarm/subscribe")]
    public async Task<IActionResult> SubscribeAlarm(string channelId)
    {
        await _gb.SubscribeAlarmAsync(channelId);
        return Ok(new { success = true });
    }

    // ---------- 平台级联 ----------

    /// <summary>级联运行状态（内存态：是否注册成功）。</summary>
    [HttpGet("cascade/status")]
    public IActionResult CascadeStatus()
        => Ok(new
        {
            enabled = _cascade.Enabled,
            localId = _cascade.LocalId,
            upperPlatforms = _cascade.Uppers.Select(u => new
            {
                name = u.Config.Name,
                ip = u.Config.Ip,
                port = u.Config.Port,
                id = u.Config.PlatformId,
                domain = u.Config.Domain,
                registered = u.Registered,
            }),
        });

    /// <summary>上级平台列表（持久化记录）。</summary>
    [HttpGet("cascade/uppers")]
    public async Task<IActionResult> CascadeUppers()
        => Ok(await _gb.GetCascadeUppersAsync());

    /// <summary>新增上级平台。</summary>
    [HttpPost("cascade/uppers")]
    public async Task<IActionResult> AddCascadeUpper([FromBody] CascadeUpper upper)
    {
        var (ok, error, added) = await _gb.AddCascadeUpperAsync(upper);
        return ok ? Ok(new { success = true, id = added!.Id }) : BadRequest(new { success = false, error });
    }

    /// <summary>更新上级平台。</summary>
    [HttpPut("cascade/uppers/{id:int}")]
    public async Task<IActionResult> UpdateCascadeUpper(int id, [FromBody] CascadeUpper upper)
    {
        var (ok, error) = await _gb.UpdateCascadeUpperAsync(id, upper);
        return ok ? Ok(new { success = true }) : BadRequest(new { success = false, error });
    }

    /// <summary>删除上级平台。</summary>
    [HttpDelete("cascade/uppers/{id:int}")]
    public async Task<IActionResult> DeleteCascadeUpper(int id)
    {
        var (ok, error) = await _gb.DeleteCascadeUpperAsync(id);
        return ok ? Ok(new { success = true }) : BadRequest(new { success = false, error });
    }
}
