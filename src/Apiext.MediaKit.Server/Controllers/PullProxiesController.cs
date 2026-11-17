using Apiext.MediaKit.Server.Auth;
using Apiext.MediaKit.Server.Contracts;
using Apiext.MediaKit.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Server.Controllers;

/// <summary>
/// 拉流代理对外 REST API（/api/v1/pull-proxies）：CRUD / 启流 / 录像开关。
/// 统一走访问令牌鉴权（「高级配置 → 访问令牌」生成）。
/// </summary>
[ApiController]
[Route("api/v1/pull-proxies")]
[ApiTokenAuth]
public sealed class PullProxiesController : ControllerBase
{
    private const string DefaultVhost = "__defaultVhost__";

    private readonly PullProxyService _pull;

    public PullProxiesController(PullProxyService pull) => _pull = pull;

    /// <summary>拉流代理列表（含在线状态、码率、观众数）。</summary>
    [HttpGet]
    public async Task<IActionResult> List()
        => Ok(await _pull.ListAsync());

    /// <summary>新增拉流代理。</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePullProxyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.App) || string.IsNullOrWhiteSpace(req.Stream) || string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "App / Stream / Url 均为必填" });

        var (ok, error) = await _pull.AddAsync(
            VhostOf(req.Vhost), req.App!, req.Stream!, req.Url!, req.AudioType,
            req.EnableOnDemand, req.EnableRecord,
            string.IsNullOrWhiteSpace(req.PullMode) ? "native" : req.PullMode,
            string.IsNullOrWhiteSpace(req.OutputCodec) ? "copy" : req.OutputCodec);

        return ok
            ? Created($"/api/v1/pull-proxies/{req.App}/{req.Stream}", null)
            : BadRequest(new { error });
    }

    /// <summary>更新拉流代理源流地址 / 输出编码 / 取流模式（native|ffmpeg，先删后加）。</summary>
    [HttpPut("{app}/{stream}")]
    public async Task<IActionResult> Update(string app, string stream, [FromBody] UpdatePullProxyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "Url 必填" });

        var (ok, error) = await _pull.UpdateAsync(
            VhostOf(req.Vhost), app, stream, req.Url!, req.AudioType,
            string.IsNullOrWhiteSpace(req.OutputCodec) ? "copy" : req.OutputCodec,
            req.PullMode);

        return ok ? NoContent() : BadRequest(new { error });
    }

    /// <summary>删除拉流代理。</summary>
    [HttpDelete("{app}/{stream}")]
    public async Task<IActionResult> Delete(string app, string stream, [FromQuery] string? vhost)
    {
        var (ok, error) = await _pull.DeleteAsync(VhostOf(vhost), app, stream);
        return ok ? NoContent() : NotFound(new { error });
    }

    /// <summary>确保流在线（按需拉流的流播放前调用）。</summary>
    [HttpPost("{app}/{stream}/start")]
    public async Task<IActionResult> Start(string app, string stream)
    {
        await _pull.EnsureStreamAsync(app, stream);
        return Ok(new { ok = true });
    }

    /// <summary>开启 / 关闭录像。</summary>
    [HttpPost("{app}/{stream}/record/{enable:bool}")]
    public async Task<IActionResult> Record(string app, string stream, bool enable)
    {
        var (ok, error) = await _pull.SetRecordAsync(app, stream, enable);
        return ok ? Ok(new { ok = true }) : BadRequest(new { error });
    }

    private static string VhostOf(string? vhost)
        => string.IsNullOrWhiteSpace(vhost) ? DefaultVhost : vhost;
}
