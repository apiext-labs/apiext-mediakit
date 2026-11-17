using System.Net.WebSockets;
using Apiext.MediaKit.Server.Auth;
using Apiext.MediaKit.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Server.Controllers;

/// <summary>
/// 播放代理（/play/*）：浏览器经应用访问流（校验访问令牌），应用转发到 ZLM。
/// 覆盖 HTTP-FLV / WS-FLV / HLS（m3u8 + 分片）；响应由本类直接写 Response，
/// 因此 Action 统一返回 <see cref="EmptyResult"/>，不改动已写入的状态码与响应体。
/// </summary>
[ApiController]
[ApiTokenAuth]
public sealed class PlayProxyController : ControllerBase
{
    private static readonly HttpClient Http = new();

    private readonly ZlmService _zlm;

    public PlayProxyController(ZlmService zlm) => _zlm = zlm;

    /// <summary>HTTP-FLV / WS-FLV（同一路径，按是否 WebSocket 升级区分）。</summary>
    [HttpGet("/play/{app}/{stream}.live.flv")]
    public async Task<IActionResult> Flv(string app, string stream)
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            await ProxyWebSocket(HttpContext, _zlm.BuildWsFlvUrl(app, stream));
            return new EmptyResult();
        }

        await ProxyHttp(HttpContext, _zlm.BuildFlvUrl(app, stream));
        return new EmptyResult();
    }

    /// <summary>HLS 索引（把分片地址改写为带令牌的代理地址）。</summary>
    [HttpGet("/play/{app}/{stream}/hls.m3u8")]
    public async Task<IActionResult> HlsIndex(string app, string stream)
    {
        var ctx = HttpContext;
        HttpResponseMessage resp;
        try
        {
            resp = await Http.GetAsync(_zlm.BuildHlsUrl(app, stream), HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();  // 客户端主动断开
        }
        catch (Exception ex)
        {
            Logger(ctx).LogError(ex, "[PlayProxy] HLS 索引拉取异常 app={App} stream={Stream}", app, stream);
            ctx.Response.StatusCode = 502;
            return new EmptyResult();
        }

        using var _ = resp;
        if (!resp.IsSuccessStatusCode)
        {
            Logger(ctx).LogWarning("[PlayProxy] HLS 索引非成功状态 {Status} app={App} stream={Stream}",
                (int)resp.StatusCode, app, stream);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            return new EmptyResult();
        }

        var content = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        var token = ApiTokenAuth.ExtractToken(ctx);
        var basePath = $"/play/{app}/{stream}/hls";

        var rewritten = content.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) return line;

            // 分片：绝对地址直接追加 token，相对地址改写为代理路径并携带 token
            if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return $"{trimmed}{(trimmed.Contains('?') ? '&' : '?')}token={token}";
            return $"{basePath}/{trimmed}?token={token}";
        });

        ctx.Response.ContentType = "application/vnd.apple.mpegurl";
        await ctx.Response.WriteAsync(string.Join('\n', rewritten), ctx.RequestAborted);
        return new EmptyResult();
    }

    /// <summary>
    /// HLS 分片。ZLM 的分片名形如「2026-11-12/20/02-48_3.ts」（含日期/小时子目录），
    /// 必须用 catch-all 参数接收，否则单段路由匹配不上会直接 404（m3u8 正常但分片全 404，播放黑屏）。
    /// </summary>
    [HttpGet("/play/{app}/{stream}/hls/{**segment}")]
    public async Task<IActionResult> HlsSegment(string app, string stream, string segment)
    {
        if (segment.Contains(".."))
        {
            HttpContext.Response.StatusCode = 400;
            return new EmptyResult();
        }

        await ProxyHttp(HttpContext, _zlm.BuildHlsSegmentUrl(app, stream, segment));
        return new EmptyResult();
    }

    private static ILogger Logger(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Apiext.MediaKit.Server.Services.PlayProxy");

    private static async Task ProxyHttp(HttpContext ctx, string target)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await Http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return;  // 客户端主动断开（切分屏/关页面很常见，不记日志）
        }
        catch (Exception ex)
        {
            Logger(ctx).LogError(ex, "[PlayProxy] 转发失败 target={Target} 来源={Ip}",
                ZlmService.SafeUrl(target), ctx.Connection.RemoteIpAddress?.ToString() ?? "-");
            ctx.Response.StatusCode = 502;
            return;
        }

        using var _ = resp;
        if (!resp.IsSuccessStatusCode)
        {
            Logger(ctx).LogWarning("[PlayProxy] ZLM 返回非成功状态 {Status} target={Target}",
                (int)resp.StatusCode, ZlmService.SafeUrl(target));
        }

        ctx.Response.StatusCode = (int)resp.StatusCode;
        if (resp.Content.Headers.ContentType != null)
            ctx.Response.ContentType = resp.Content.Headers.ContentType.ToString();
        if (resp.Content.Headers.ContentLength != null)
            ctx.Response.ContentLength = resp.Content.Headers.ContentLength;

        try
        {
            await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // 播放器关闭，正常现象
        }
    }

    private static async Task ProxyWebSocket(HttpContext ctx, string wsUrl)
    {
        var logger = Logger(ctx);
        using var upstream = new ClientWebSocket();
        try
        {
            await upstream.ConnectAsync(new Uri(wsUrl), ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PlayProxy] WS 连接 ZLM 失败 target={Target}", ZlmService.SafeUrl(wsUrl));
            ctx.Response.StatusCode = 502;
            return;
        }

        using var downstream = await ctx.WebSockets.AcceptWebSocketAsync();

        var buffer = new byte[64 * 1024];
        try
        {
            await Task.WhenAll(
                Pump(downstream, upstream, buffer, ctx.RequestAborted),
                Pump(upstream, downstream, buffer, ctx.RequestAborted));
        }
        catch (OperationCanceledException)
        {
            // 播放器关闭，正常现象
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "[PlayProxy] WS 转发结束 target={Target}", ZlmService.SafeUrl(wsUrl));
        }
    }

    private static async Task Pump(WebSocket from, WebSocket to, byte[] buffer, CancellationToken ct)
    {
        while (from.State == WebSocketState.Open && to.State == WebSocketState.Open)
        {
            var result = await from.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await to.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                return;
            }
            await to.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }
}
