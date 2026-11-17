using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Apiext.MediaKit.Server.Gb28181;
using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// ZLMediaKit WebHook 处理：能力令牌校验 + 各事件业务逻辑。
/// hook 请求体只带 mediaServerId / hook_index，ZLM 不提供签名，
/// 因此每个 URL 携带自己的 cap = base64url(HMAC(secret, mediaServerId + "\n" + event))，
/// 再叠加 mediaServerId 校验、来源 IP 白名单与限流。
/// </summary>
public class ZlmHookService
{
    private static readonly ConcurrentDictionary<string, RateWindow> RateBuckets = new();

    private sealed class RateWindow
    {
        public int Count;
        public DateTime WindowStart;
    }

    private readonly AppDbContext _db;
    private readonly PullProxyService _pull;
    private readonly PlayTokenService _playToken;
    private readonly ZlmOnlineCache _online;
    private readonly ZlmHookOptions _opt;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ZlmHookService> _logger;

    public ZlmHookService(
        AppDbContext db,
        PullProxyService pull,
        PlayTokenService playToken,
        ZlmOnlineCache online,
        IOptions<ZlmHookOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<ZlmHookService> logger)
    {
        _db = db;
        _pull = pull;
        _playToken = playToken;
        _online = online;
        _opt = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public ZlmHookOptions Options => _opt;

    // ---------- 验签 ----------

    /// <summary>校验 hook 请求来源；失败返回原因（不区分细节，避免信息泄露）。</summary>
    public (bool Ok, string Reason) Verify(HttpContext http, ZlmHookRequest body, string hookEvent)
    {
        if (!_opt.Enabled) return (false, "hook disabled");

        // 1) 能力令牌：每个事件独立，泄露一个 URL 不影响其它事件
        var provided = http.Request.Query["cap"].ToString();
        var expected = Capability(hookEvent);
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected) ||
            provided.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
        {
            return (false, "invalid capability");
        }

        // 2) mediaServerId：防止同网段其它 ZLM 实例打进来
        if (!string.IsNullOrEmpty(_opt.MediaServerId) &&
            !string.Equals(body.MediaServerId, _opt.MediaServerId, StringComparison.Ordinal))
        {
            return (false, "mediaServerId mismatch");
        }

        // 3) 来源 IP 白名单（IPv4-mapped IPv6 需归一化）
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return (false, "unknown client ip");
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (!_opt.AllowedIps.Any(a => IPAddress.TryParse(a, out var allow) && allow.Equals(ip)))
            return (false, "client ip not allowed");

        // 4) 轻量限流：防公网刷 hook 端点（3000 次/分钟/IP）
        if (!AllowRate(ip.ToString())) return (false, "rate limited");

        return (true, string.Empty);
    }

    /// <summary>计算某事件的 hook 能力令牌（部署脚本用同样的算法写入 config.ini）。</summary>
    public string Capability(string hookEvent)
    {
        if (string.IsNullOrEmpty(_opt.CapabilitySecret)) return string.Empty;
        var payload = Encoding.UTF8.GetBytes($"{_opt.MediaServerId}\n{hookEvent}");
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_opt.CapabilitySecret), payload);
        return PlayTokenService.Base64UrlEncode(mac);
    }

    private static bool AllowRate(string key, int limitPerMinute = 3000)
    {
        var now = DateTime.UtcNow;
        var bucket = RateBuckets.GetOrAdd(key, _ => new RateWindow { WindowStart = now });
        lock (bucket)
        {
            if ((now - bucket.WindowStart).TotalMinutes >= 1)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }
            bucket.Count++;
            return bucket.Count <= limitPerMinute;
        }
    }

    // ---------- ZLM 启动 ----------

    /// <summary>on_server_started：ZLM 重启后清空在线缓存，避免残留流长期误报在线。</summary>
    public void OnServerStarted(ZlmHookRequest body)
    {
        var cleared = _online.Count;
        _online.Clear();
        _logger.LogInformation("[Hook] ZLM 已启动（{Node}），清理在线缓存 {Count} 条", body.MediaServerId, cleared);
    }

    // ---------- 录像切片入库 ----------

    /// <summary>on_record_mp4：MP4 切片完成，写入录像索引（幂等，ZLM 可能重试投递）。</summary>
    public async Task OnRecordMp4Async(ZlmHookRequest body)
    {
        if (string.IsNullOrEmpty(body.App) || string.IsNullOrEmpty(body.Stream) || string.IsNullOrEmpty(body.FileName))
        {
            _logger.LogDebug("[Hook] on_record_mp4 参数不完整，忽略");
            return;
        }

        var exists = await _db.RecordingFiles.AnyAsync(r =>
            r.App == body.App && r.Stream == body.Stream && r.FileName == body.FileName);
        if (exists) return;

        var now = DateTime.UtcNow;
        var startUtc = body.StartTime is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(body.StartTime.Value).UtcDateTime
            : now;

        _db.RecordingFiles.Add(new RecordingFile
        {
            Vhost = string.IsNullOrEmpty(body.Vhost) ? "__defaultVhost__" : body.Vhost,
            App = body.App,
            Stream = body.Stream,
            FileName = body.FileName,
            Folder = body.Folder ?? string.Empty,
            FilePath = body.FilePath ?? string.Empty,
            Url = body.Url ?? string.Empty,
            FileSize = body.FileSize ?? 0,
            DurationSec = (long)Math.Round(body.TimeLen ?? 0),
            StartTimeUtc = startUtc,
            Source = RecordingFile.SourceHook,
            LastSeenAtUtc = now,
            CreatedAt = now,
        });

        try
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation("[Hook] 录像入库 {App}/{Stream} {File}", body.App, body.Stream, body.FileName);
        }
        catch (DbUpdateException)
        {
            // 并发或重试导致的重复插入：唯一索引兜底，忽略即可
        }
    }

    // ---------- 无人观看断流 ----------

    /// <summary>
    /// on_stream_none_reader：只接管「按需拉流且未录像」的代理，返回 true 让 ZLM 直接关流；
    /// GB28181 点播流、其它流一律不处理。
    /// </summary>
    public async Task<bool> OnNoneReaderAsync(string? app, string? stream)
    {
        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(stream)) return false;

        var proxy = await _db.PullProxies.FirstOrDefaultAsync(p => p.App == app && p.Stream == stream);
        if (proxy is null || !proxy.EnableOnDemand || proxy.EnableRecord) return false;

        try
        {
            await _pull.StopStreamAsync(proxy);
            _logger.LogInformation("[Hook] 无人观看断流 {App}/{Stream}", app, stream);
            return false; // 代理已删除，流会自然关闭，无需 ZLM 再 close
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hook] 无人观看断流失败 {App}/{Stream}，交由 ZLM 关闭", app, stream);
            return true;
        }
    }

    // ---------- 流不存在（按需拉流兜底） ----------

    /// <summary>
    /// on_stream_not_found：有人播放但流不存在时，若该路是「按需拉流」代理则拉起。
    /// 返回 close=false 不关闭播放器，前端播放器重连即可看到画面。
    /// </summary>
    public async Task<bool> OnStreamNotFoundAsync(string? app, string? stream)
    {
        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(stream)) return false;

        var proxy = await _db.PullProxies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.App == app && p.Stream == stream && p.EnableOnDemand);
        if (proxy is null) return false;

        _logger.LogInformation("[Hook] 流不存在，按需拉起 {App}/{Stream}", app, stream);
        try
        {
            await _pull.EnsureStreamAsync(app, stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hook] 按需拉起失败 {App}/{Stream}", app, stream);
        }

        return false;
    }

    // ---------- 推流鉴权 ----------

    /// <summary>
    /// on_publish：放行已知流（GB28181 收流 app=rtp、已登记的拉流代理）。
    /// EnforcePublishAuth=false 时为观察模式：未知推流只记日志不拒绝。
    /// </summary>
    public async Task<bool> OnPublishAsync(ZlmHookRequest body)
    {
        if (string.Equals(body.App, "rtp", StringComparison.OrdinalIgnoreCase)) return true;

        if (string.IsNullOrEmpty(body.App) || string.IsNullOrEmpty(body.Stream)) return false;

        if (await _db.PullProxies.AnyAsync(p => p.App == body.App && p.Stream == body.Stream)) return true;

        if (_opt.EnforcePublishAuth)
        {
            _logger.LogWarning("[Hook] 拒绝未知推流 {Label} origin={Origin} ip={Ip}",
                body.MediaLabel, body.OriginTypeStr, body.Ip);
            return false;
        }

        _logger.LogInformation("[Hook/Observe] 未知推流 {Label} origin={Origin} ip={Ip}",
            body.MediaLabel, body.OriginTypeStr, body.Ip);
        return true;
    }

    // ---------- 播放鉴权 ----------

    /// <summary>
    /// on_play：HTTP / WS / RTMP / WebRTC 播放鉴权。
    /// 优先认短期播放令牌 `play_token`（站内播放器自动签发）；
    /// 其次认长期「访问令牌」`token`（ZLM 直连地址分享给第三方时使用，永久有效、
    /// 可在「高级配置 → 访问令牌」随时撤销）。
    /// RTSP 不携带令牌，放行（ZLM 抓拍与部分内部拉流走 RTSP）。
    /// </summary>
    public async Task<(bool Allow, string Reason)> OnPlayAsync(ZlmHookRequest body)
    {
        var schema = body.Schema ?? string.Empty;
        if (!IsTokenProtectedSchema(schema))
        {
            _logger.LogDebug("[Hook] on_play 不校验（schema={Schema}）{Label}", schema, body.MediaLabel);
            return (true, string.Empty);
        }

        var app = body.App ?? string.Empty;
        var stream = body.Stream ?? string.Empty;

        var playToken = _playToken.ExtractFromParams(body.Params);
        var (ok, reason) = _playToken.Verify(playToken, app, stream, body.Ip);
        if (ok) return (true, string.Empty);

        // 直连地址（不经平台代理）改用访问令牌
        var apiToken = ExtractQueryValue(body.Params, "token");
        if (!string.IsNullOrEmpty(apiToken) && await IsValidApiTokenAsync(apiToken))
        {
            _logger.LogDebug("[Hook] on_play 访问令牌通过 {Label}", body.MediaLabel);
            return (true, string.Empty);
        }

        _logger.LogWarning("[Hook] 播放被拒 {Label} schema={Schema} ip={Ip}：{Reason}",
            body.MediaLabel, schema, body.Ip, reason);
        return (false, reason);
    }

    /// <summary>访问令牌是否有效（启用中且未过期）。</summary>
    private async Task<bool> IsValidApiTokenAsync(string token)
    {
        var now = DateTime.UtcNow;
        return await _db.ApiTokens.AnyAsync(t =>
            t.Token == token && t.Enabled && (t.ExpiresAt == null || t.ExpiresAt > now));
    }

    /// <summary>从 ZLM 回调的 query 串里取指定参数。</summary>
    private static string? ExtractQueryValue(string? rawParams, string key)
    {
        if (string.IsNullOrEmpty(rawParams)) return null;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(rawParams.TrimStart('?'));
        return query.TryGetValue(key, out var value) ? value.ToString() : null;
    }

    /// <summary>
    /// 需要令牌的播放协议。
    /// 注意：**HTTP-FLV 播放时 ZLM 上报的 schema 是 rtmp**（实测 `[Hook] on_play ... schema=rtmp`），
    /// 不是 http —— 之前只认 http，导致直连 FLV 不带任何令牌也能播放。
    /// **WebRTC 播放上报的 schema 是 `rtc`**（实测 params 形如
    /// `app=live&amp;stream=101&amp;type=play&amp;token=xxx&amp;session=18-28`；返回非 0 时 ZLM 回
    /// `code:-400 [auth failed]`，播放确实被拒），漏掉 rtc 会让直连 WebRTC 完全绕过校验。
    /// **HLS 索引（m3u8）也会回调 on_play**（实测 schema=`hls` / `hls.fmp4`），首个请求带 token 时
    /// `params` 就是该 query（实测 `"params":"token=966a156f..."`），所以按前缀匹配纳入。
    /// 但要注意 ZLM 的 HlsPlayer 是**流级复用**：播放器存活（约 segDur×segNum，实测 3~5 秒内的连续
    /// 请求会复用）期间新客户端直接共享、**不再回调 on_play**——只要有客户端在播（hls.js 每 2 秒轮询
    /// m3u8 续期），新加入者就绕过校验。即 HLS 的校验是「播放器级」，只拦得住无人观看时的冷启动请求；
    /// 需要严格逐客户端鉴权时用 FLV / WS-FLV / WebRTC，或走平台代理（代理层每次都校验访问令牌）。
    /// ts/fmp4 分片走 on_http_access 且不带 query，无法校验。
    /// rtsp 故意不纳入：ZLM 抓拍（getSnap）与部分内部拉流走 RTSP，纳入会让抓拍失败。
    /// </summary>
    private static bool IsTokenProtectedSchema(string schema)
        => schema is "http" or "https" or "ws" or "wss" or "rtmp" or "rtmps" or "rtc"
           || schema.StartsWith("hls", StringComparison.OrdinalIgnoreCase);

    // ---------- 在线状态缓存 ----------

    /// <summary>on_stream_changed：流注册/注销，更新在线缓存。</summary>
    public void OnStreamChanged(ZlmHookRequest body)
    {
        if (!_opt.StreamChangedCache || body.Regist is null) return;

        if (body.Regist == true)
        {
            _online.MarkOnline(body.Vhost, body.App, body.Stream, body.Schema);
        }
        else
        {
            _online.MarkOffline(body.Vhost, body.App, body.Stream);
        }
    }

    // ---------- HTTP 文件访问鉴权 ----------

    /// <summary>
    /// on_http_access：只对受保护前缀（默认 /record/）校验路径级播放令牌；
    /// HLS 分片不带 query，不能拦截。
    /// </summary>
    public (bool Allow, string? Error) OnHttpAccess(ZlmHookRequest body)
    {
        if (!_opt.HttpAccessAuth) return (true, null);

        var path = body.Path ?? string.Empty;
        if (!_opt.HttpAccessGuardedPrefixes.Any(p =>
                path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return (true, null);
        }

        var token = _playToken.ExtractFromParams(body.Params);
        var (ok, reason) = _playToken.VerifyPath(token, path);
        if (!ok)
        {
            _logger.LogWarning("[Hook] 文件访问被拒 path={Path} ip={Ip}：{Reason}", path, body.Ip, reason);
        }

        return (ok, ok ? null : reason);
    }

    // ---------- RTP 收流超时 ----------

    /// <summary>
    /// on_rtp_server_timeout：GB28181 点播/回放开了收流端口但设备未推流（或推流中断），
    /// 收尾对应 SIP 会话并关闭端口。
    /// </summary>
    public async Task OnRtpServerTimeoutAsync(ZlmHookRequest body)
    {
        var streamId = body.EffectiveStream;
        _logger.LogWarning("[Hook] RTP 收流超时 stream={Stream} port={Port} ssrc={Ssrc} tcp={Tcp}",
            streamId, body.LocalPort, body.Ssrc, body.TcpMode);

        if (string.IsNullOrEmpty(streamId)) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var gb = scope.ServiceProvider.GetRequiredService<Gb28181Service>();
            await gb.StopPlayAsync(streamId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hook] 收流超时清理失败 stream={Stream}", streamId);
        }
    }

    // ---------- 其余事件（按官方文档补全，未参与业务的只做记录） ----------

    /// <summary>最近一次 on_server_keepalive 时间（UTC），null 表示尚未收到。</summary>
    public static DateTime? LastKeepaliveUtc { get; private set; }

    /// <summary>已收到的心跳次数（进程内累计）。</summary>
    public static long KeepaliveCount { get; private set; }

    /// <summary>on_server_exited：ZLM 进程退出（崩溃/重启），只记告警；在线缓存留给 on_server_started 清理。</summary>
    public void OnServerExited(ZlmHookRequest body)
        => _logger.LogWarning("[Hook] ZLM 进程退出（{Node}），等待重启后的 on_server_started", body.MediaServerId);

    /// <summary>on_server_keepalive：心跳（默认 30s 一次），用于判断 ZLM 是否卡死。</summary>
    public void OnServerKeepalive(ZlmHookRequest body)
    {
        KeepaliveCount++;
        LastKeepaliveUtc = DateTime.UtcNow;

        // 心跳很频繁，只在前几次和每 100 次记一条 Debug，避免刷日志
        if (KeepaliveCount <= 3 || KeepaliveCount % 100 == 0)
        {
            _logger.LogDebug("[Hook] 心跳 #{Count}（{Node}）", KeepaliveCount, body.MediaServerId);
        }
    }

    /// <summary>
    /// on_rtsp_realm：是否给该 rtsp 流启用摘要鉴权。
    /// 返回空 realm = 不启用，ZLM 继续走 url 参数（on_play）鉴权，
    /// 否则 VLC/PotPlayer 拉取与平台抓拍（getSnap 走 RTSP）都会被 401 挡住。
    /// </summary>
    public string OnRtspRealm(ZlmHookRequest body)
    {
        _logger.LogDebug("[Hook] on_rtsp_realm 不启用专属鉴权 {Label} ip={Ip}", body.MediaLabel, body.Ip);
        return string.Empty;
    }

    /// <summary>on_rtsp_auth：仅在 on_rtsp_realm 返回非空 realm 后触发；本平台不启用，一律拒绝。</summary>
    public (bool Allow, string Msg) OnRtspAuth(ZlmHookRequest body)
    {
        _logger.LogWarning("[Hook] 拒绝 RTSP 摘要鉴权 {Label} user={User} ip={Ip}（未启用 rtsp realm）",
            body.MediaLabel, body.UserName, body.Ip);
        return (false, "rtsp digest auth disabled");
    }

    /// <summary>on_shell_login：telnet shell 登录鉴权，默认拒绝（ZlmHook:AllowShellLogin=true 才放行）。</summary>
    public (bool Allow, string Msg) OnShellLogin(ZlmHookRequest body)
    {
        if (_opt.AllowShellLogin)
        {
            _logger.LogWarning("[Hook] 放行 telnet shell 登录 user={User} ip={Ip}", body.UserName, body.Ip);
            return (true, "success");
        }

        _logger.LogWarning("[Hook] 拒绝 telnet shell 登录 user={User} ip={Ip}", body.UserName, body.Ip);
        return (false, "shell login disabled");
    }

    /// <summary>on_flow_report：连接断开且流量超过 general.flowThreshold 时回调，仅记录。</summary>
    public void OnFlowReport(ZlmHookRequest body)
        => _logger.LogDebug("[Hook] 流量统计 {Label} {Role} schema={Schema} bytes={Bytes} duration={Seconds}s ip={Ip}",
            body.MediaLabel, body.Player == true ? "播放" : "推流", body.Schema, body.TotalBytes, body.Duration, body.Ip);

    /// <summary>on_record_ts：TS 切片完成，仅记录（回放以 mp4 索引为准）。</summary>
    public void OnRecordTs(ZlmHookRequest body)
        => _logger.LogDebug("[Hook] TS 录像完成 {Label} {File} size={Size}", body.MediaLabel, body.FileName, body.FileSize);

    /// <summary>on_send_rtp_stopped：国标级联 RTP 转推停止，仅记录。</summary>
    public void OnSendRtpStopped(ZlmHookRequest body)
        => _logger.LogInformation("[Hook] RTP 转推停止 {Label} ssrc={Ssrc}", body.MediaLabel, body.Ssrc);
}
