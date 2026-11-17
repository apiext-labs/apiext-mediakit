using System.Text.Json;
using Apiext.MediaKit.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Server.Controllers;

/// <summary>
/// ZLM WebHook 单一入口：<c>POST /api/zlm/hook/{event}</c>，按 route 参数分发到各事件处理。
/// <para>
/// 约定：
/// <list type="bullet">
/// <item>ZLM 以 POST + JSON body 投递，URL 携带该事件专属的 <c>?cap=</c> 能力令牌
/// （cap = base64url(HMAC-SHA256(CapabilitySecret, mediaServerId + "\n" + event))），
/// 因此 <c>event</c> 必须原样取 route 值参与验签，config.ini 里的 URL 无需变化。</item>
/// <item>响应固定 HTTP 200（避免 ZLM 触发重试），业务结果放在 body 的 <c>code</c> 字段；
/// 未知事件回 <c>code:404</c>、body 非法回 <c>code:400</c>、验签失败回 <c>code:403</c>。</item>
/// <item>body 手工反序列化（不用 <c>[FromBody]</c>），这样畸形 JSON 也能回 HTTP 200 + <c>code:400</c>，
/// 不会变成 MVC 的 400 ProblemDetails。</item>
/// </list>
/// </para>
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/zlm/hook")]
public sealed class ZlmHookController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ZlmHookService _hooks;
    private readonly ILogger<ZlmHookController> _logger;

    public ZlmHookController(ZlmHookService hooks, ILogger<ZlmHookController> logger)
    {
        _hooks = hooks;
        _logger = logger;
    }

    /// <summary>ZLM 事件入口：/api/zlm/hook/on_server_started、on_play、on_publish ……</summary>
    /// <param name="hookEvent">事件名（on_*），与 ZLM 的 <c>hook.on_*</c> 配置键同名。</param>
    [HttpPost("{hookEvent}")]
    public async Task<IActionResult> Handle(string hookEvent)
    {
        // 1) 事件名白名单：未知事件直接拒，避免无意义反序列化与日志噪音
        if (!ZlmHookEvents.All.Contains(hookEvent, StringComparer.Ordinal))
        {
            _logger.LogWarning("[Hook] 未知事件 {Event} 来源={Ip}",
                hookEvent, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-");
            return new JsonResult(new { code = 404, msg = "unknown hook event" });
        }

        // 2) body 手工解析：畸形/空 body 也要回 HTTP 200 + code:400
        ZlmHookRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ZlmHookRequest>(
                Request.Body, JsonOptions, HttpContext.RequestAborted);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Hook] body 解析失败 {Event}", hookEvent);
            return new JsonResult(new { code = 400, msg = "invalid json body" });
        }

        if (body is null) return new JsonResult(new { code = 400, msg = "empty body" });

        // 3) 能力令牌 + mediaServerId + 来源 IP + 限流（事件名取 route 值参与 HMAC）
        var (ok, reason) = _hooks.Verify(HttpContext, body, hookEvent);
        if (!ok) return new JsonResult(new { code = 403, msg = reason });

        // 4) 按事件分发
        switch (hookEvent)
        {
            // ---------- 服务器生命周期 ----------

            // ZLM 启动：清空在线缓存
            case ZlmHookEvents.ServerStarted:
                _hooks.OnServerStarted(body);
                return new JsonResult(new { code = 0 });

            // ZLM 进程退出：只记录（崩溃/重启排障）
            case ZlmHookEvents.ServerExited:
                _hooks.OnServerExited(body);
                return new JsonResult(new { code = 0 });

            // 心跳（默认 30s 一次，hook.alive_interval 可调）
            case ZlmHookEvents.ServerKeepalive:
                _hooks.OnServerKeepalive(body);
                return new JsonResult(new { code = 0, msg = "success" });

            // ---------- 流事件 ----------

            // 流注册/注销：维护在线缓存
            case ZlmHookEvents.StreamChanged:
                _hooks.OnStreamChanged(body);
                return new JsonResult(new { code = 0 });

            // 无人观看：按需断流
            case ZlmHookEvents.StreamNoneReader:
            {
                var close = await _hooks.OnNoneReaderAsync(body.App, body.Stream);
                return new JsonResult(new ZlmNoneReaderReply { Code = 0, Close = close });
            }

            // 流不存在：按需拉流兜底（ZLM 只读 close，不会自行拉流）
            case ZlmHookEvents.StreamNotFound:
            {
                var close = await _hooks.OnStreamNotFoundAsync(body.App, body.Stream);
                return new JsonResult(new ZlmNoneReaderReply { Code = 0, Close = close });
            }

            // ---------- 鉴权类 ----------

            // 推流鉴权
            case ZlmHookEvents.Publish:
            {
                var allow = await _hooks.OnPublishAsync(body);
                return new JsonResult(new ZlmPublishReply
                {
                    Code = allow ? 0 : 403,
                    Msg = allow ? null : "stream not allowed",
                });
            }

            // 播放鉴权（播放令牌 / 访问令牌）
            case ZlmHookEvents.Play:
            {
                var (allow, reason2) = await _hooks.OnPlayAsync(body);
                return new JsonResult(new ZlmAuthReply
                {
                    Code = allow ? 0 : 403,
                    Msg = allow ? null : reason2,
                });
            }

            // RTSP 专属鉴权（realm）：平台不启用，返回空 realm → ZLM 回落到 on_play
            case ZlmHookEvents.RtspRealm:
                return new JsonResult(new ZlmRtspRealmReply { Code = 0, Realm = _hooks.OnRtspRealm(body) });

            // RTSP 摘要鉴权：只有 realm 非空才触发；平台不启用，一律拒绝
            case ZlmHookEvents.RtspAuth:
            {
                var (allow, msg) = _hooks.OnRtspAuth(body);
                return new JsonResult(new ZlmRtspAuthReply
                {
                    Code = allow ? 0 : 403,
                    Msg = msg,
                    Encrypted = false,
                    Passwd = string.Empty,
                });
            }

            // telnet shell 登录鉴权（默认拒绝）
            case ZlmHookEvents.ShellLogin:
            {
                var (allow, msg) = _hooks.OnShellLogin(body);
                return new JsonResult(new ZlmAuthReply { Code = allow ? 0 : 403, Msg = msg });
            }

            // HTTP 文件访问鉴权（录像等受保护路径）
            case ZlmHookEvents.HttpAccess:
            {
                var (allow, error) = _hooks.OnHttpAccess(body);
                return new JsonResult(new ZlmHttpAccessReply
                {
                    Code = 0,
                    Err = allow ? string.Empty : (error ?? "访问被拒绝"),
                    Path = string.Empty,
                    Second = 0,
                });
            }

            // ---------- 录像 / 统计 ----------

            // MP4 切片完成：录像索引落库
            case ZlmHookEvents.RecordMp4:
                await _hooks.OnRecordMp4Async(body);
                return new JsonResult(new { code = 0 });

            // TS 切片完成：只记录（回放以 mp4 索引为准）
            case ZlmHookEvents.RecordTs:
                _hooks.OnRecordTs(body);
                return new JsonResult(new { code = 0 });

            // 流量统计（断连且流量超阈值时回调）
            case ZlmHookEvents.FlowReport:
                _hooks.OnFlowReport(body);
                return new JsonResult(new { code = 0, msg = "success" });

            // ---------- RTP / 级联 ----------

            // RTP 收流超时：国标点播收尾
            case ZlmHookEvents.RtpServerTimeout:
                await _hooks.OnRtpServerTimeoutAsync(body);
                return new JsonResult(new { code = 0 });

            // RTP 转推停止：国标级联结束
            case ZlmHookEvents.SendRtpStopped:
                _hooks.OnSendRtpStopped(body);
                return new JsonResult(new { code = 0 });

            default:
                // 已由白名单拦截，走到这里说明 ZlmHookEvents.All 与 switch 不同步
                _logger.LogWarning("[Hook] 事件已在白名单但未分发 {Event}", hookEvent);
                return new JsonResult(new { code = 404, msg = "unhandled hook event" });
        }
    }
}
