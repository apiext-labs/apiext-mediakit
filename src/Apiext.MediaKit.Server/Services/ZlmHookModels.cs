using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// ZLM 受管 hook 事件（与 ZLM 的 hook.* 配置键一一对应）。
/// 实测本机版本（fb32657）共 17 个可配置事件；on_proxy_publish / on_proxy_connect /
/// on_stream_not_found_ffmpeg 在该版本**不存在**，不要往 config.ini 里写（setServerConfig 对未知键会报错）。
/// </summary>
public static class ZlmHookEvents
{
    public const string ServerStarted = "on_server_started";
    public const string ServerExited = "on_server_exited";
    public const string ServerKeepalive = "on_server_keepalive";
    public const string FlowReport = "on_flow_report";
    public const string HttpAccess = "on_http_access";
    public const string Play = "on_play";
    public const string Publish = "on_publish";
    public const string RecordMp4 = "on_record_mp4";
    public const string RecordTs = "on_record_ts";
    public const string RtspRealm = "on_rtsp_realm";
    public const string RtspAuth = "on_rtsp_auth";
    public const string SendRtpStopped = "on_send_rtp_stopped";
    public const string ShellLogin = "on_shell_login";
    public const string StreamChanged = "on_stream_changed";
    public const string StreamNoneReader = "on_stream_none_reader";
    public const string StreamNotFound = "on_stream_not_found";
    public const string RtpServerTimeout = "on_rtp_server_timeout";

    public static readonly string[] All =
    {
        ServerStarted, ServerExited, ServerKeepalive, FlowReport, HttpAccess,
        Play, Publish, RecordMp4, RecordTs, RtspRealm, RtspAuth,
        SendRtpStopped, ShellLogin, StreamChanged, StreamNoneReader,
        StreamNotFound, RtpServerTimeout,
    };
}

/// <summary>ZLM WebHook 接入配置（appsettings 的 ZlmHook 段）。</summary>
public class ZlmHookOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 能力令牌根密钥。每个 hook URL 携带自己的 cap：
    /// cap = base64url(HMAC-SHA256(CapabilitySecret, mediaServerId + "\n" + event))。
    /// 单个 URL 泄露只会暴露对应事件，且换节点/换事件即失效。
    /// </summary>
    public string CapabilitySecret { get; set; } = string.Empty;

    /// <summary>期望的 mediaServerId（与 config.ini 的 general.mediaServerId 一致）。</summary>
    public string MediaServerId { get; set; } = "apiext-mediakit";

    /// <summary>允许的来源 IP（同容器部署为 127.0.0.1）。</summary>
    public List<string> AllowedIps { get; set; } = new() { "127.0.0.1", "::1" };

    /// <summary>按需断流交给 on_stream_none_reader 接管（开启后 OnDemandPullService 轮询不启动）。</summary>
    public bool ReplaceOnDemandPolling { get; set; } = true;

    /// <summary>推流鉴权是否强制拒绝未知推流（false=只记日志）。</summary>
    public bool EnforcePublishAuth { get; set; } = true;

    /// <summary>HTTP 文件访问鉴权：仅对 <see cref="HttpAccessGuardedPrefixes"/> 命中的路径校验播放令牌。</summary>
    public bool HttpAccessAuth { get; set; } = true;

    /// <summary>
    /// 受保护的 HTTP 路径前缀。HLS 分片请求不带 query（m3u8 里是相对路径），
    /// 因此不能一刀切拦截，默认只保护录像目录。
    /// </summary>
    public List<string> HttpAccessGuardedPrefixes { get; set; } = new() { "/record/" };

    /// <summary>是否用 on_stream_changed 维护在线状态缓存。</summary>
    public bool StreamChangedCache { get; set; } = true;

    /// <summary>
    /// 是否放行 ZLM 的 telnet shell 登录（默认 false：9000 端口可执行任意调试命令，
    /// 属于后门级入口，只有排障时才临时打开）。
    /// </summary>
    public bool AllowShellLogin { get; set; }
}

/// <summary>播放令牌配置（appsettings 的 PlayToken 段）。</summary>
public class PlayTokenOptions
{
    /// <summary>签名密钥（≥32 字节，未配置时播放令牌功能自动关闭）。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>令牌有效期（秒）。ZLM 只在 on_play 时校验一次。</summary>
    public int TtlSeconds { get; set; } = 1800;

    /// <summary>过期后的宽限（秒），覆盖播放器断线重连，默认 12 小时。</summary>
    public int GraceSeconds { get; set; } = 43200;

    /// <summary>是否把令牌绑定到签发时的客户端 IP。</summary>
    public bool BindClientIp { get; set; }
}

/// <summary>
/// ZLM hook 请求体（已覆盖官方文档 17 个事件的全部字段）。
/// 注意 ZLM 字段命名混用：mediaServerId 为驼峰，
/// start_time/file_size/time_len/file_path/file_name/hook_index/local_port/stream_id/user_name 等为下划线，必须显式映射；
/// header.*（on_http_access）与 data（on_server_keepalive）等动态键通过 <see cref="Unknown"/> 兜底捕获。
/// </summary>
public sealed class ZlmHookRequest
{
    [JsonPropertyName("mediaServerId")] public string? MediaServerId { get; set; }
    [JsonPropertyName("hook_index")] public long? HookIndex { get; set; }

    // 通用流标识
    public string? Schema { get; set; }
    public string? Vhost { get; set; }
    public string? App { get; set; }
    public string? Stream { get; set; }
    public string? Params { get; set; }
    public string? Ip { get; set; }
    public int? Port { get; set; }
    public string? Id { get; set; }

    // on_publish / on_stream_changed
    public int? OriginType { get; set; }
    public string? OriginTypeStr { get; set; }
    public string? OriginUrl { get; set; }
    public bool? Regist { get; set; }
    public long? AliveSecond { get; set; }
    public long? BytesSpeed { get; set; }
    public long? CreateStamp { get; set; }
    public int? ReaderCount { get; set; }
    public int? TotalReaderCount { get; set; }

    // on_flow_report
    public int? Duration { get; set; }
    public bool? Player { get; set; }
    public long? TotalBytes { get; set; }

    // on_record_mp4 / on_record_ts（file_path 与 on_http_access 复用：都是本地绝对路径）
    [JsonPropertyName("start_time")] public long? StartTime { get; set; }
    // ZLM 的 time_len 是浮点秒（例如 30.000000），声明成整数会让整个 hook 反序列化失败
    [JsonPropertyName("time_len")] public double? TimeLen { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("file_path")] public string? FilePath { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    public string? Folder { get; set; }
    public string? Url { get; set; }

    // on_http_access
    public string? Path { get; set; }
    [JsonPropertyName("is_dir")] public bool? IsDir { get; set; }

    // on_rtp_server_timeout（ssrc / tcp_mode 是**数字**，声明成 string 会让整个事件解析失败）
    [JsonPropertyName("local_port")] public int? LocalPort { get; set; }
    [JsonPropertyName("stream_id")] public string? StreamId { get; set; }
    public long? Ssrc { get; set; }
    [JsonPropertyName("tcp_mode")] public int? TcpMode { get; set; }
    [JsonPropertyName("re_use_port")] public bool? ReUsePort { get; set; }

    // on_rtsp_realm / on_rtsp_auth / on_shell_login
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
    public string? Realm { get; set; }
    /// <summary>on_rtsp_auth / on_shell_login 的密码字段（文档 on_shell_login 示例里笔误标成 bool，实际是字符串）。</summary>
    public string? Passwd { get; set; }
    [JsonPropertyName("must_no_encrypt")] public bool? MustNoEncrypt { get; set; }
    public bool? Encrypted { get; set; }

    // on_server_keepalive：各对象存活实例计数
    [JsonPropertyName("data")] public JsonElement? InstanceCounts { get; set; }

    /// <summary>未显式映射的字段（header.*、tracks、originSock 等），避免新增字段时反序列化丢数据。</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>流标识（日志用）。</summary>
    public string MediaLabel => $"{Vhost ?? "__defaultVhost__"}/{App}/{Stream}";

    /// <summary>on_rtp_server_timeout 用 stream_id，其它事件用 stream。</summary>
    public string? EffectiveStream => StreamId ?? Stream;
}

// ================= 各事件的回复模型（字段与官方文档一一对应） =================

/// <summary>on_play / on_shell_login 等通用鉴权回复。</summary>
public class ZlmAuthReply
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
}

/// <summary>on_rtsp_realm 回复：realm 为空表示不启用 RTSP 专属鉴权。</summary>
public class ZlmRtspRealmReply
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("realm")] public string Realm { get; set; } = string.Empty;
}

/// <summary>on_rtsp_auth 回复：encrypted=密码是摘要还是明文，passwd=明文或 md5(user:realm:password)。</summary>
public class ZlmRtspAuthReply
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("encrypted")] public bool Encrypted { get; set; }
    [JsonPropertyName("passwd")] public string Passwd { get; set; } = string.Empty;
}

/// <summary>on_stream_none_reader 回复：close=true 让 ZLM 关闭该流。</summary>
public class ZlmNoneReaderReply
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("close")] public bool Close { get; set; }
}

/// <summary>on_http_access 回复：path 为授权/禁止的顶端目录（空=当前目录），second 为有效期秒。</summary>
public class ZlmHttpAccessReply
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("err")] public string Err { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("second")] public int Second { get; set; }
}

/// <summary>
/// on_publish 的完整回复（文档列出的全部可选开关）。
/// 本项目默认只回 code/msg，其余字段保持 null 不输出（<see cref="JsonIgnoreCondition.WhenWritingNull"/>）。
/// </summary>
public class ZlmPublishReply
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }

    [JsonPropertyName("enable_hls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableHls { get; set; }

    [JsonPropertyName("enable_hls_fmp4"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableHlsFmp4 { get; set; }

    [JsonPropertyName("enable_mp4"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableMp4 { get; set; }

    [JsonPropertyName("enable_rtsp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableRtsp { get; set; }

    [JsonPropertyName("enable_rtmp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableRtmp { get; set; }

    [JsonPropertyName("enable_ts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableTs { get; set; }

    [JsonPropertyName("enable_fmp4"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableFmp4 { get; set; }

    [JsonPropertyName("hls_demand"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HlsDemand { get; set; }

    [JsonPropertyName("rtsp_demand"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RtspDemand { get; set; }

    [JsonPropertyName("rtmp_demand"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RtmpDemand { get; set; }

    [JsonPropertyName("ts_demand"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TsDemand { get; set; }

    [JsonPropertyName("fmp4_demand"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Fmp4Demand { get; set; }

    [JsonPropertyName("enable_audio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableAudio { get; set; }

    [JsonPropertyName("add_mute_audio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AddMuteAudio { get; set; }

    [JsonPropertyName("mp4_save_path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mp4SavePath { get; set; }

    [JsonPropertyName("mp4_max_second"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Mp4MaxSecond { get; set; }

    [JsonPropertyName("mp4_as_player"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Mp4AsPlayer { get; set; }

    [JsonPropertyName("hls_save_path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HlsSavePath { get; set; }

    [JsonPropertyName("modify_stamp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ModifyStamp { get; set; }

    [JsonPropertyName("continue_push_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? ContinuePushMs { get; set; }

    [JsonPropertyName("auto_close"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoClose { get; set; }

    [JsonPropertyName("stream_replace"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StreamReplace { get; set; }
}
