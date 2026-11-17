using System.Diagnostics;
using Apiext.MediaKit.Server.Apis;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// ZLM 业务封装：统一注入 secret，对上层隐藏鉴权细节。
/// 所有调用经 <see cref="GuardAsync{T}"/> 包一层：ZLM 返回 code≠0 记 Warning、抛异常记 Error，便于排查“为什么没画面/没拉流”。
/// </summary>
public class ZlmService
{
    private readonly IZlMediaKitApi _api;
    private readonly PlayTokenService _playToken;
    private readonly ILogger<ZlmService> _logger;
    private readonly string _apiBaseUrl;
    private readonly string _secret;
    private readonly string _playBaseUrl;
    private readonly string _playHost;
    private readonly int _httpPort;
    private readonly int _rtspPort;
    private readonly int _rtmpPort;
    private readonly int _internalRtmpPort;
    private readonly string _ffmpegCmdTemplate;
    private readonly string _ffmpegDstTemplate;
    private readonly string _ffmpegCmdH264;
    private readonly string _ffmpegCmdH265;

    public ZlmService(IZlMediaKitApi api, PlayTokenService playToken, IConfiguration config, ILogger<ZlmService> logger)
    {
        _api = api;
        _playToken = playToken;
        _logger = logger;
        _secret = config["ZlMediaKit:Secret"] ?? string.Empty;
        _apiBaseUrl = (config["ZlMediaKit:BaseUrl"] ?? "http://127.0.0.1:28180").TrimEnd('/');

        // 对外播放基址：优先 PlayBaseUrl；否则 PublicHost + PublicHttpPort；再否则自动探测本机 IP。
        // 目的是「默认配置就能用」，不用每次部署手填 IP。
        var configuredPlayBase = config["ZlMediaKit:PlayBaseUrl"];
        if (string.IsNullOrWhiteSpace(configuredPlayBase))
        {
            var publicHost = config["ZlMediaKit:PublicHost"];
            if (string.IsNullOrWhiteSpace(publicHost))
            {
                publicHost = NetworkInfo.DetectOutboundIPv4();
                PublicHostAutoDetected = true;
            }

            var publicPort = config.GetValue("ZlMediaKit:PublicHttpPort", 28180);
            PublicHost = publicHost;
            _playBaseUrl = $"http://{publicHost}:{publicPort}";
            PlayBaseUrlSource = "auto";
        }
        else
        {
            _playBaseUrl = configuredPlayBase.TrimEnd('/');
            PublicHost = new Uri(_playBaseUrl).Host;
            PlayBaseUrlSource = "config";
        }

        // 容器内自动探测到的通常是 172.x 容器地址，浏览器/设备不可达
        if (PublicHostAutoDetected && NetworkInfo.IsInContainer())
        {
            _logger.LogWarning("[ZLM] 未配置 ZlMediaKit:PublicHost/PlayBaseUrl，自动探测到容器地址 {Ip}；" +
                               "请把 PublicHost 设为宿主机对客户端可达的 IP，否则生成的播放地址不可用", PublicHost);
        }

        var uri = new Uri(_playBaseUrl);
        _playHost = uri.Host;
        _httpPort = uri.Port;
        _rtspPort = config.GetValue("ZlMediaKit:RtspPort", 28554);
        _rtmpPort = config.GetValue("ZlMediaKit:RtmpPort", 28335);

        // ZLM 容器内部端口（FFmpeg 在 ZLM 容器内推流用），须与 config.ini 的 [rtmp] port 一致
        _internalRtmpPort = int.TryParse(config["ZlMediaKit:InternalRtmpPort"], out var ir) ? ir : 28335;
        _ffmpegCmdTemplate = config["PullProxy:FfmpegCmd"] ?? "ffmpeg -i {src} -c copy -f flv {dst}";
        _ffmpegDstTemplate = config["PullProxy:FfmpegDst"] ?? "rtmp://127.0.0.1:{port}/{app}/{stream}";

        // H.264 / H.265 转码命令模板：可配置 preset / 码率等参数，缺省为低延迟软编
        _ffmpegCmdH264 = config["PullProxy:FfmpegCmdH264"]
            ?? "ffmpeg -re -i {src} -c:v libx264 -preset veryfast -tune zerolatency -c:a aac -ar 44100 -ac 2 -f flv {dst}";
        _ffmpegCmdH265 = config["PullProxy:FfmpegCmdH265"]
            ?? "ffmpeg -re -i {src} -c:v libx265 -preset veryfast -tune zerolatency -c:a aac -ar 44100 -ac 2 -f flv {dst}";
    }

    /// <summary>ZLM API 基址（应用内部访问用）。</summary>
    public string BaseUrl => _apiBaseUrl;

    /// <summary>对外播放基址（生成播放地址用）。</summary>
    public string PlayBaseUrl => _playBaseUrl;

    /// <summary>对外播放主机（PlayBaseUrl 的 host）。</summary>
    public string PublicHost { get; private set; } = "";

    /// <summary>PublicHost 是否为自动探测值（未显式配置）。</summary>
    public bool PublicHostAutoDetected { get; private set; }

    /// <summary>PlayBaseUrl 生效方式：config / auto。</summary>
    public string PlayBaseUrlSource { get; private set; } = "config";

    /// <summary>脱敏 URL 查询串：play_token / token / secret 不落日志。</summary>
    public static string SafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "-";
        var idx = url.IndexOf('?');
        return idx < 0 ? url : url[..idx] + "?***";
    }

    /// <summary>脱敏源流地址：隐藏 URL 中的账号密码（rtsp://user:pass@host/...），日志用。</summary>
    public static string SafeSourceUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "-";
        var masked = System.Text.RegularExpressions.Regex.Replace(url, @"(?<=://)[^/@\s]+(?=@)", "***");
        return SafeUrl(masked);
    }

    // 失败日志节流：ZLM 长时间不可用时（监控 3 秒轮询）避免把日志刷爆
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, int Skipped)> _failureGate = new();
    private static readonly TimeSpan FailureLogWindow = TimeSpan.FromSeconds(30);

    /// <summary>判断本次失败是否需要落日志，并返回窗口内被抑制的次数。</summary>
    private (bool Log, int Skipped) NoteFailure(string api)
    {
        var now = DateTime.UtcNow;
        if (_failureGate.TryGetValue(api, out var gate) && now - gate.At < FailureLogWindow)
        {
            _failureGate[api] = (gate.At, gate.Skipped + 1);
            return (false, gate.Skipped + 1);
        }

        _failureGate[api] = (now, 0);
        return (true, gate.Skipped);
    }

    /// <summary>统一包装 ZLM 调用：code≠0 记 Warning（含 code/msg/耗时），异常记 Error 并继续抛出。</summary>
    private async Task<T> GuardAsync<T>(string api, Func<Task<T>> call) where T : class, IZlmResult
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await call();
            if (result.Code != 0)
            {
                var (log, skipped) = NoteFailure(api);
                if (log)
                {
                    _logger.LogWarning("[ZLM] {Api} 返回失败 code={Code} msg={Msg} ({Elapsed}ms){Skipped}",
                        api, result.Code, result.Msg ?? "-", sw.ElapsedMilliseconds,
                        skipped > 0 ? $"，窗口内另有 {skipped} 次同类失败已省略" : string.Empty);
                }
            }
            else
            {
                _logger.LogDebug("[ZLM] {Api} ok ({Elapsed}ms)", api, sw.ElapsedMilliseconds);
            }
            return result;
        }
        catch (Exception ex)
        {
            var (log, skipped) = NoteFailure(api);
            if (log)
            {
                _logger.LogError(ex, "[ZLM] {Api} 调用异常 ({Elapsed}ms){Skipped}",
                    api, sw.ElapsedMilliseconds,
                    skipped > 0 ? $"，窗口内另有 {skipped} 次同类失败已省略" : string.Empty);
            }
            throw;
        }
    }

    public Task<ZlmResponse<List<MediaItem>>> GetMediaListAsync(
        string? vhost = null, string? app = null, string? stream = null)
        => GuardAsync("getMediaList", () => _api.GetMediaListAsync(_secret, vhost, app, stream));

    /// <summary>
    /// 查询在线流的视频编码：返回 <c>H264</c> / <c>H265</c>（统一大写，未知编码原样返回），
    /// 流不在线或 ZLM 不可达时返回 null。用于播放端决定走 MSE 硬解还是 WASM 软解。
    /// </summary>
    public async Task<string?> GetVideoCodecAsync(string? vhost, string app, string stream)
    {
        var resp = await GetMediaListAsync(vhost, app, stream);
        if (resp.Code != 0 || resp.Data is not { Count: > 0 }) return null;

        var media = resp.Data.FirstOrDefault(m =>
            string.Equals(m.Stream, stream, StringComparison.OrdinalIgnoreCase)) ?? resp.Data[0];

        // codec_type：0 视频 / 1 音频
        var track = media.Tracks?.FirstOrDefault(t => t.CodecType == 0);
        if (track is null) return null;

        // ZLM 的 codec_id_name 实测可靠（H264 / H265），优先用它
        var name = track.CodecIdName?.Trim().ToUpperInvariant();
        if (name is "HEVC" or "H.265" or "H265") return "H265";
        if (name is "AVC" or "H.264" or "H264") return "H264";

        // 兜底：ZLM 自己的 CodecId 枚举是 0 = H264(AVC)、1 = H265(HEVC)，
        // 不是 FLV/Jessibuca 的 7 / 12，别按那边写
        return track.CodecId switch { 0 => "H264", 1 => "H265", _ => name };
    }

    /// <summary>HTTP-FLV 播放地址（自动附加播放令牌，ZLM on_play 校验）。</summary>
    public string BuildFlvUrl(string app, string stream)
        => _playToken.Decorate($"{_playBaseUrl}/{app}/{stream}.live.flv", app, stream);

    /// <summary>WebSocket-FLV 播放地址（自动附加播放令牌）。</summary>
    public string BuildWsFlvUrl(string app, string stream)
        => _playToken.Decorate($"ws://{_playHost}:{_httpPort}/{app}/{stream}.live.flv", app, stream);

    /// <summary>
    /// WebRTC 裸信令地址（ZLM webrtc 接口，媒体走 UDP）。**不含任何令牌**，
    /// 调用方必须自行追加 `play_token=`（短期）或 `token=`（长期访问令牌）——ZLM 对
    /// schema=rtc 的播放同样回调 on_play 校验，无令牌会被拒。
    /// </summary>
    public string BuildWebRtcUrl(string app, string stream)
        => $"{_playBaseUrl}/index/api/webrtc?app={app}&stream={stream}&type=play";

    /// <summary>WebRTC 播放地址（自动附加播放令牌，ZLM on_play 校验）。</summary>
    public string BuildWebRtcPlayUrl(string app, string stream)
        => _playToken.Decorate(BuildWebRtcUrl(app, stream), app, stream);

    /// <summary>
    /// HLS 索引地址（m3u8 附播放令牌）。ZLM 的 m3u8 请求会回调 on_play，首个请求的 `params` 带该 query，
    /// 因此能被令牌校验；但 HlsPlayer 是流级复用，播放器存活期间新客户端不再回调 on_play（令牌校验只在
    /// 冷启动生效）。要逐客户端严格鉴权请用 FLV / WebRTC 或平台代理 `/play/.../{hls.m3u8}`。
    /// </summary>
    public string BuildHlsUrl(string app, string stream)
        => _playToken.Decorate($"{_playBaseUrl}/{app}/{stream}/hls.m3u8", app, stream);

    /// <summary>HLS 分片地址（m3u8 内引用的 ts 文件，与 m3u8 同目录）。</summary>
    public string BuildHlsSegmentUrl(string app, string stream, string segment)
        => $"{_playBaseUrl}/{app}/{stream}/{segment}";

    /// <summary>RTSP 播放地址（第三方播放器复制用，查询串不保证被保留，不附令牌）。</summary>
    public string BuildRtspUrl(string app, string stream)
        => $"rtsp://{_playHost}:{_rtspPort}/{app}/{stream}";

    /// <summary>RTMP 播放地址（同上）。</summary>
    public string BuildRtmpUrl(string app, string stream)
        => $"rtmp://{_playHost}:{_rtmpPort}/{app}/{stream}";

    /// <summary>汇总一个流的多协议播放地址集合。</summary>
    public PlayAddresses BuildPlayAddresses(string app, string stream)
        => new()
        {
            Flv = BuildFlvUrl(app, stream),
            WsFlv = BuildWsFlvUrl(app, stream),
            Hls = BuildHlsUrl(app, stream),
            Rtsp = BuildRtspUrl(app, stream),
            Rtmp = BuildRtmpUrl(app, stream),
            WebRtc = BuildWebRtcPlayUrl(app, stream),
        };

    // ---------- 官方 RESTful 接口的完整封装（业务暂未全部使用，留作扩展/排障） ----------

    public Task<ZlmResponse<List<string>>> GetApiListAsync()
        => GuardAsync("getApiList", () => _api.GetApiListAsync(_secret));

    public Task<VersionResult> GetVersionAsync()
        => GuardAsync("version", () => _api.GetVersionAsync(_secret));

    public Task<SimpleResult> RestartServerAsync()
        => GuardAsync("restartServer", () => _api.RestartServerAsync(_secret));

    public Task<CloseStreamResult> CloseStreamAsync(string app, string stream,
        string schema = "rtmp", string vhost = "__defaultVhost__", bool force = false)
        => GuardAsync("close_stream", () => _api.CloseStreamAsync(_secret, schema, vhost, app, stream, force ? 1 : 0));

    public Task<CloseStreamsResult> CloseStreamsAsync(string? app = null, string? stream = null,
        string? schema = null, bool force = false)
        => GuardAsync("close_streams", () => _api.CloseStreamsAsync(_secret, schema, "__defaultVhost__", app, stream, force ? 1 : 0));

    public Task<SimpleResult> KickSessionAsync(string id)
        => GuardAsync("kick_session", () => _api.KickSessionAsync(_secret, id));

    public Task<KickSessionsResult> KickSessionsAsync(int? localPort = null, string? peerIp = null)
        => GuardAsync("kick_sessions", () => _api.KickSessionsAsync(_secret, localPort, peerIp));

    public Task<IsMediaOnlineResult> IsMediaOnlineAsync(string app, string stream, string schema = "rtmp")
        => GuardAsync("isMediaOnline", () => _api.IsMediaOnlineAsync(_secret, schema, "__defaultVhost__", app, stream));

    public Task<MediaInfoResult> GetMediaInfoAsync(string app, string stream, string schema = "rtmp")
        => GuardAsync("getMediaInfo", () => _api.GetMediaInfoAsync(_secret, schema, "__defaultVhost__", app, stream));

    public Task<ZlmResponse<List<RtpServerItem>>> ListRtpServerAsync()
        => GuardAsync("listRtpServer", () => _api.ListRtpServerAsync(_secret));

    public Task<StartSendRtpPassiveResult> StartSendRtpPassiveAsync(string streamId, string ssrc, int? srcPort = null)
        => GuardAsync("startSendRtpPassive", () => _api.StartSendRtpPassiveAsync(
            _secret, "__defaultVhost__", "rtp", streamId, ssrc, srcPort, null, null, null));

    public Task<ZlmResponse<AddStreamPusherProxyData>> AddStreamPusherProxyAsync(string app, string stream,
        string dstUrl, string schema = "rtmp")
        => GuardAsync("addStreamPusherProxy", () => _api.AddStreamPusherProxyAsync(
            _secret, "__defaultVhost__", schema, app, stream, dstUrl));

    public Task<ZlmResponse<object>> DelStreamPusherProxyAsync(string key)
        => GuardAsync("delStreamPusherProxy", () => _api.DelStreamPusherProxyAsync(_secret, key));

    public Task<ZlmResponse<List<MediaPlayerItem>>> GetMediaPlayerListAsync(string app, string stream, string schema = "rtmp")
        => GuardAsync("getMediaPlayerList", () => _api.GetMediaPlayerListAsync(_secret, schema, "__defaultVhost__", app, stream));

    public Task<SimpleResult> PauseRtpCheckAsync(string streamId)
        => GuardAsync("pauseRtpCheck", () => _api.PauseRtpCheckAsync(_secret, streamId));

    public Task<SimpleResult> ResumeRtpCheckAsync(string streamId)
        => GuardAsync("resumeRtpCheck", () => _api.ResumeRtpCheckAsync(_secret, streamId));

    /// <summary>
    /// 截图：ZLM 该接口**返回 JPEG 字节流**（不是 JSON），失败时才回 JSON 错误，故不套 GuardAsync，
    /// 调用方需自行判断内容类型/长度。
    /// </summary>
    public Task<byte[]> GetSnapBytesAsync(string app, string stream, int timeoutSec = 5, int expireSec = 30)
        => _api.GetSnapAsync(_secret, $"rtsp://127.0.0.1:{_rtspPort}/{app}/{stream}", timeoutSec, expireSec);

    /// <summary>下载 ZLM 容器内的 bin 文件（返回原始字节）。</summary>
    public Task<byte[]> DownloadBinAsync(string path) => _api.DownloadBinAsync(_secret, path);

    /// <summary>WebRTC SDP 交换（媒体面走 UDP；播放鉴权由 on_play 的 schema=rtc 负责）。</summary>
    public Task<WebRtcAnswer> WebRtcAsync(string app, string stream, string sdp, string type = "play")
        => GuardAsync("webrtc", () => _api.WebRtcAsync(app, stream, type, sdp));

    public Task<ZlmResponse<List<Dictionary<string, string>>>> GetServerConfigAsync()
        => GuardAsync("getServerConfig", () => _api.GetServerConfigAsync(_secret));

    public Task<SetServerConfigResult> SetServerConfigAsync(IReadOnlyDictionary<string, string> configs)
    {
        var body = string.Join("&", configs.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return GuardAsync("setServerConfig", () => _api.SetServerConfigAsync(_secret, body));
    }

    public Task<ZlmResponse<StatisticData>> GetStatisticAsync()
        => GuardAsync("getStatistic", () => _api.GetStatisticAsync(_secret));

    public Task<ZlmResponse<AddStreamProxyData>> AddStreamProxyAsync(
        string vhost, string app, string stream, string url, int? audioType)
    {
        var (enableAudio, addMuteAudio) = AudioParams(audioType);
        return GuardAsync("addStreamProxy", () => _api.AddStreamProxyAsync(_secret, vhost, app, stream, url, enableAudio, addMuteAudio));
    }

    public Task<ZlmResponse<object>> DelStreamProxyAsync(string key)
        => GuardAsync("delStreamProxy", () => _api.DelStreamProxyAsync(_secret, key));

    /// <summary>
    /// FFmpeg 拉流：ZLM 用 config.ini 的 [ffmpeg] cmd 模板生成命令，参数只传源地址与推流目标。
    /// 本项目模板为「视频 copy + 音频转 AAC」，用于把 G.711(PCMA/PCMU) 源转成浏览器可播的 AAC。
    /// </summary>
    public Task<ZlmResponse<AddFFmpegSourceData>> AddFFmpegSourceAsync(string srcUrl, string dstUrl, int timeoutMs = 15000)
        => GuardAsync("addFFmpegSource", () => _api.AddFFmpegSourceAsync(_secret, srcUrl, dstUrl, timeoutMs, 0, 0));

    public Task<ZlmResponse<object>> DelFFmpegSourceAsync(string key)
        => GuardAsync("delFFmpegSource", () => _api.DelFFmpegSourceAsync(_secret, key));

    /// <summary>ZLM 容器内的 RTMP 推流地址（FFmpeg 推流目标）。</summary>
    public string BuildInternalRtmpUrl(string app, string stream)
        => _ffmpegDstTemplate
            .Replace("{port}", _internalRtmpPort.ToString())
            .Replace("{app}", app)
            .Replace("{stream}", stream);

    /// <summary>
    /// 仅供排障时展示参考：ZLM 的 addFFmpegSource 不接受自定义命令，
    /// 实际命令来自 config.ini 的 [ffmpeg] cmd 模板（见 deploy/docker/config.ini：视频 copy + 音频转 AAC）。
    /// </summary>
    public string BuildFfmpegCmd(string app, string stream, string src, string codec = "copy")
    {
        var dst = BuildInternalRtmpUrl(app, stream);

        if (codec is "h264" or "h265")
        {
            var template = codec == "h264" ? _ffmpegCmdH264 : _ffmpegCmdH265;
            return template
                .Replace("{src}", $"\"{src}\"")
                .Replace("{dst}", $"\"{dst}\"");
        }

        return _ffmpegCmdTemplate
            .Replace("{src}", $"\"{src}\"")
            .Replace("{dst}", $"\"{dst}\"");
    }

    public Task<ZlmResponse<List<PullProxyItem>>> ListStreamProxyAsync()
        => GuardAsync("listStreamProxy", () => _api.ListStreamProxyAsync(_secret));

    public Task<ZlmResponse<List<ThreadLoadItem>>> GetThreadsLoadAsync()
        => GuardAsync("getThreadsLoad", () => _api.GetThreadsLoadAsync(_secret));

    public Task<ZlmResponse<List<ThreadLoadItem>>> GetWorkThreadsLoadAsync()
        => GuardAsync("getWorkThreadsLoad", () => _api.GetWorkThreadsLoadAsync(_secret));

    public Task<ZlmResponse<List<SessionItem>>> GetAllSessionAsync(int? localPort = null, string? peerIp = null)
        => GuardAsync("getAllSession", () => _api.GetAllSessionAsync(_secret, localPort, peerIp));

    /// <summary>打开 RTP 收流端口（port=0 自动分配）</summary>
    public Task<OpenRtpServerResult> OpenRtpServerAsync(string streamId, int port = 0, int tcpMode = 0, string? ssrc = null)
        => GuardAsync("openRtpServer", () => _api.OpenRtpServerAsync(_secret, port, streamId, tcpMode, ssrc, null, null));

    public Task<CloseRtpServerResult> CloseRtpServerAsync(string streamId)
        => GuardAsync("closeRtpServer", () => _api.CloseRtpServerAsync(_secret, streamId));

    public Task<ZlmResponse<RtpInfo>> GetRtpInfoAsync(string streamId)
        => GuardAsync("getRtpInfo", () => _api.GetRtpInfoAsync(_secret, streamId));

    /// <summary>RTP 推流（级联：把本地 rtp 收流转发到上级平台）</summary>
    public Task<ZlmResponse<object>> StartSendRtpAsync(string streamId, string ssrc, string dstIp, int dstPort, int isUdp = 1)
        => GuardAsync("startSendRtp", () => _api.StartSendRtpAsync(_secret, "__defaultVhost__", "rtp", streamId, ssrc, dstIp, dstPort, isUdp, null));

    /// <summary>停止 RTP 推流（级联）</summary>
    public Task<ZlmResponse<object>> StopSendRtpAsync(string streamId, string ssrc)
        => GuardAsync("stopSendRtp", () => _api.StopSendRtpAsync(_secret, "__defaultVhost__", "rtp", streamId, ssrc));

    /// <summary>RTP 收流后的播放地址（app 固定为 rtp）</summary>
    public string BuildRtpFlvUrl(string streamId)
        => _playToken.Decorate($"{_playBaseUrl}/rtp/{streamId}.live.flv", "rtp", streamId);

    /// <summary>
    /// 快照抓拍地址：ZLM 拉自身流并返回 JPEG。前端以 &lt;img src&gt; 直接访问。
    /// 注意：该 URL 含 secret，禁止写入日志。
    /// </summary>
    public string BuildSnapUrl(string app, string stream, int timeoutSec = 5, int expireSec = 30)
    {
        // 抓拍经 ZLM 内部 RTSP 拉流：端口用配置的 RTSP 端口（与 config.ini 的 [rtsp] port 一致，已改为非常用端口）
        var rtspUrl = $"rtsp://127.0.0.1:{_rtspPort}/{app}/{stream}";
        return $"{_playBaseUrl}/index/api/getSnap?secret={_secret}" +
               $"&url={Uri.EscapeDataString(rtspUrl)}" +
               $"&timeout_sec={timeoutSec}&expire_sec={expireSec}";
    }

    // ---------- 本地录像 ----------

    public Task<RecordResult> StartRecordAsync(string app, string stream, int maxSecond = 0)
        => GuardAsync("startRecord", () => _api.StartRecordAsync(_secret, 1, "__defaultVhost__", app, stream, maxSecond));

    public Task<RecordResult> StopRecordAsync(string app, string stream)
        => GuardAsync("stopRecord", () => _api.StopRecordAsync(_secret, 1, "__defaultVhost__", app, stream));

    public Task<IsRecordingResult> IsRecordingAsync(string app, string stream)
        => GuardAsync("isRecording", () => _api.IsRecordingAsync(_secret, 1, "__defaultVhost__", app, stream));

    public Task<ZlmResponse<Mp4RecordList>> ListMp4RecordAsync(string app, string stream, string period)
        => GuardAsync("getMp4RecordFile", () => _api.GetMp4RecordFileAsync(_secret, "__defaultVhost__", app, stream, period));

    /// <summary>录像文件地址（路径级令牌绑定，ZLM on_http_access 校验）。</summary>
    public string BuildRecordUrl(string app, string stream, string date, string fileName)
    {
        var path = $"/record/{app}/{stream}/{date}/{fileName}";
        return _playToken.DecoratePath($"{_playBaseUrl}{path}", path);
    }

    /// <summary>音频参数映射（0=无音频 1=启用 2=静音音频），与参考实现保持一致</summary>
    private static (int?, int?) AudioParams(int? audioType)
    {
        return audioType switch
        {
            0 => (0, 0),
            1 => (1, 0),
            2 => (1, 1),
            _ => ((int?)null, (int?)null)
        };
    }
}

/// <summary>一个流的多协议播放地址集合。</summary>
public class PlayAddresses
{
    public string Flv { get; set; } = string.Empty;
    public string WsFlv { get; set; } = string.Empty;
    public string Hls { get; set; } = string.Empty;
    public string Rtsp { get; set; } = string.Empty;
    public string Rtmp { get; set; } = string.Empty;
    public string WebRtc { get; set; } = string.Empty;
}
