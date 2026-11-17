using WebApiClientCore;
using WebApiClientCore.Attributes;

namespace Apiext.MediaKit.Server.Apis;

/// <summary>
/// ZLMediaKit HTTP API 声明式封装（WebApiClientCore）。
/// ZLM 的 /index/api/* 均为 GET + secret query 鉴权，返回 {code, msg, data}。
/// </summary>
public interface IZlMediaKitApi : IHttpApi
{
    /// <summary>获取在线流列表</summary>
    [HttpGet("/index/api/getMediaList")]
    Task<ZlmResponse<List<MediaItem>>> GetMediaListAsync(
        [PathQuery] string secret,
        [PathQuery] string? vhost = null,
        [PathQuery] string? app = null,
        [PathQuery] string? stream = null);

    /// <summary>获取服务器配置（data[0] 为所有配置项的 map）</summary>
    [HttpGet("/index/api/getServerConfig")]
    Task<ZlmResponse<List<Dictionary<string, string>>>> GetServerConfigAsync([PathQuery] string secret);

    /// <summary>设置服务器配置（body 为已编码的 urlencoded 键值对）</summary>
    [HttpPost("/index/api/setServerConfig")]
    Task<SetServerConfigResult> SetServerConfigAsync(
        [PathQuery] string secret,
        [RawFormContent] string body);

    /// <summary>获取服务器统计信息</summary>
    [HttpGet("/index/api/getStatistic")]
    Task<ZlmResponse<StatisticData>> GetStatisticAsync([PathQuery] string secret);

    /// <summary>添加拉流代理</summary>
    [HttpGet("/index/api/addStreamProxy")]
    Task<ZlmResponse<AddStreamProxyData>> AddStreamProxyAsync(
        [PathQuery] string secret,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] string url,
        [PathQuery] int? enable_audio = null,
        [PathQuery] int? add_mute_audio = null);

    /// <summary>删除拉流代理</summary>
    [HttpGet("/index/api/delStreamProxy")]
    Task<ZlmResponse<object>> DelStreamProxyAsync([PathQuery] string secret, [PathQuery] string key);

    /// <summary>
    /// 添加 FFmpeg 拉流代理（ffmpeg 进程拉流后推流到 dst_url，兼容性更强）。
    /// 注意：ZLM 的参数名是 src_url（源流地址），命令由 config.ini 的 [ffmpeg] cmd 模板生成，
    /// 不能把整条 ffmpeg 命令当参数传进来（曾误写 ffmpeg_cmd → ZLM 报 Required parameter missed）。
    /// </summary>
    [HttpGet("/index/api/addFFmpegSource")]
    Task<ZlmResponse<AddFFmpegSourceData>> AddFFmpegSourceAsync(
        [PathQuery] string secret,
        [PathQuery] string src_url,
        [PathQuery] string dst_url,
        [PathQuery] int timeout_ms,
        [PathQuery] int? enable_hls = null,
        [PathQuery] int? enable_mp4 = null);

    /// <summary>删除 FFmpeg 拉流代理（key 为 addFFmpegSource 返回的 key，即 dst_url）</summary>
    [HttpGet("/index/api/delFFmpegSource")]
    Task<ZlmResponse<object>> DelFFmpegSourceAsync([PathQuery] string secret, [PathQuery] string key);

    /// <summary>拉流代理列表</summary>
    [HttpGet("/index/api/listStreamProxy")]
    Task<ZlmResponse<List<PullProxyItem>>> ListStreamProxyAsync([PathQuery] string secret);

    /// <summary>获取事件轮询线程负载（0-100）</summary>
    [HttpGet("/index/api/getThreadsLoad")]
    Task<ZlmResponse<List<ThreadLoadItem>>> GetThreadsLoadAsync([PathQuery] string secret);

    /// <summary>获取后台工作线程负载（0-100）</summary>
    [HttpGet("/index/api/getWorkThreadsLoad")]
    Task<ZlmResponse<List<ThreadLoadItem>>> GetWorkThreadsLoadAsync([PathQuery] string secret);

    /// <summary>获取所有在线会话（可按本地端口/远端 IP 筛选，用于观众列表）</summary>
    [HttpGet("/index/api/getAllSession")]
    Task<ZlmResponse<List<SessionItem>>> GetAllSessionAsync(
        [PathQuery] string secret,
        [PathQuery] int? local_port = null,
        [PathQuery] string? peer_ip = null);

    /// <summary>打开 RTP 收流端口（GB28181 点播收流，port=0 自动分配）</summary>
    [HttpGet("/index/api/openRtpServer")]
    Task<OpenRtpServerResult> OpenRtpServerAsync(
        [PathQuery] string secret,
        [PathQuery] int port,
        [PathQuery] string stream_id,
        [PathQuery] int? tcp_mode = null,
        [PathQuery] string? ssrc = null,
        [PathQuery] int? only_audio = null,
        [PathQuery] string? local_ip = null);

    /// <summary>关闭 RTP 收流端口</summary>
    [HttpGet("/index/api/closeRtpServer")]
    Task<CloseRtpServerResult> CloseRtpServerAsync(
        [PathQuery] string secret,
        [PathQuery] string stream_id);

    /// <summary>查询 RTP 收流状态</summary>
    [HttpGet("/index/api/getRtpInfo")]
    Task<ZlmResponse<RtpInfo>> GetRtpInfoAsync(
        [PathQuery] string secret,
        [PathQuery] string stream_id);

    /// <summary>RTP 推流（GB28181 平台级联：把本地收流转发到上级平台的收流地址）</summary>
    [HttpGet("/index/api/startSendRtp")]
    Task<ZlmResponse<object>> StartSendRtpAsync(
        [PathQuery] string secret,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] string ssrc,
        [PathQuery] string dst_url,
        [PathQuery] int dst_port,
        [PathQuery] int is_udp,
        [PathQuery] int? src_port = null);

    /// <summary>停止 RTP 推流</summary>
    [HttpGet("/index/api/stopSendRtp")]
    Task<ZlmResponse<object>> StopSendRtpAsync(
        [PathQuery] string secret,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] string ssrc);

    /// <summary>开始录制（type: 0=hls, 1=mp4）</summary>
    [HttpGet("/index/api/startRecord")]
    Task<RecordResult> StartRecordAsync(
        [PathQuery] string secret,
        [PathQuery] int type,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] int? max_second = null);

    /// <summary>停止录制</summary>
    [HttpGet("/index/api/stopRecord")]
    Task<RecordResult> StopRecordAsync(
        [PathQuery] string secret,
        [PathQuery] int type,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream);

    /// <summary>查询是否在录制</summary>
    [HttpGet("/index/api/isRecording")]
    Task<IsRecordingResult> IsRecordingAsync(
        [PathQuery] string secret,
        [PathQuery] int type,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream);

    /// <summary>
    /// 查询 MP4 录像文件列表（period 为日期，如 2020-01-24）。
    /// 注意：接口名是 **getMp4RecordFile**，写成 listMp4Record 会 404（实测 ZLM fb32657）。
    /// </summary>
    [HttpGet("/index/api/getMp4RecordFile")]
    Task<ZlmResponse<Mp4RecordList>> GetMp4RecordFileAsync(
        [PathQuery] string secret,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] string period);

    // ================= 以下按官方 RESTful 接口全量补全（业务暂未全部使用） =================

    /// <summary>列出本机所有 API 路径（排障：确认某接口在当前版本是否存在）</summary>
    [HttpGet("/index/api/getApiList")]
    Task<ZlmResponse<List<string>>> GetApiListAsync([PathQuery] string secret);

    /// <summary>ZLM 版本信息（branchName / buildTime / commitHash）</summary>
    [HttpGet("/index/api/version")]
    Task<VersionResult> GetVersionAsync([PathQuery] string secret);

    /// <summary>重启 ZLM（仅 Daemon 方式有效，否则进程直接退出）</summary>
    [HttpGet("/index/api/restartServer")]
    Task<SimpleResult> RestartServerAsync([PathQuery] string secret);

    /// <summary>关闭单个流（已过期接口，新代码用 close_streams）</summary>
    [HttpGet("/index/api/close_stream")]
    Task<CloseStreamResult> CloseStreamAsync(
        [PathQuery] string secret,
        [PathQuery] string schema,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] int? force = null);

    /// <summary>按条件批量关闭流（force=1 时连有人观看的也关）</summary>
    [HttpGet("/index/api/close_streams")]
    Task<CloseStreamsResult> CloseStreamsAsync(
        [PathQuery] string secret,
        [PathQuery] string? schema = null,
        [PathQuery] string? vhost = null,
        [PathQuery] string? app = null,
        [PathQuery] string? stream = null,
        [PathQuery] int? force = null);

    /// <summary>踢掉单个 TCP 会话（id 来自 getAllSession）</summary>
    [HttpGet("/index/api/kick_session")]
    Task<SimpleResult> KickSessionAsync([PathQuery] string secret, [PathQuery] string id);

    /// <summary>按本机端口 / 对端 IP 批量踢会话</summary>
    [HttpGet("/index/api/kick_sessions")]
    Task<KickSessionsResult> KickSessionsAsync(
        [PathQuery] string secret,
        [PathQuery] int? local_port = null,
        [PathQuery] string? peer_ip = null);

    /// <summary>流是否在线（已过期接口，新代码用 getMediaList）</summary>
    [HttpGet("/index/api/isMediaOnline")]
    Task<IsMediaOnlineResult> IsMediaOnlineAsync(
        [PathQuery] string secret,
        [PathQuery] string schema,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream);

    /// <summary>单条流详细信息（已过期接口）</summary>
    [HttpGet("/index/api/getMediaInfo")]
    Task<MediaInfoResult> GetMediaInfoAsync(
        [PathQuery] string secret,
        [PathQuery] string schema,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream);

    /// <summary>当前打开的 RTP 收流端口列表</summary>
    [HttpGet("/index/api/listRtpServer")]
    Task<ZlmResponse<List<RtpServerItem>>> ListRtpServerAsync([PathQuery] string secret);

    /// <summary>RTP 被动收流（GB28181 级联 TCP 被动模式，等待上级连入后推流）</summary>
    [HttpGet("/index/api/startSendRtpPassive")]
    Task<StartSendRtpPassiveResult> StartSendRtpPassiveAsync(
        [PathQuery] string secret,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] string ssrc,
        [PathQuery] int? src_port = null,
        [PathQuery] int? pt = null,
        [PathQuery] int? use_ps = null,
        [PathQuery] int? only_audio = null);

    /// <summary>添加转推代理（把本地流转推到远端 rtsp/rtmp，key 用于删除）</summary>
    [HttpGet("/index/api/addStreamPusherProxy")]
    Task<ZlmResponse<AddStreamPusherProxyData>> AddStreamPusherProxyAsync(
        [PathQuery] string secret,
        [PathQuery] string vhost,
        [PathQuery] string schema,
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] string dst_url,
        [PathQuery] int? retry_count = null,
        [PathQuery] int? rtp_type = null,
        [PathQuery] double? timeout_sec = null);

    /// <summary>删除转推代理</summary>
    [HttpGet("/index/api/delStreamPusherProxy")]
    Task<ZlmResponse<object>> DelStreamPusherProxyAsync([PathQuery] string secret, [PathQuery] string key);

    /// <summary>某路流当前的播放器列表（谁在看）</summary>
    [HttpGet("/index/api/getMediaPlayerList")]
    Task<ZlmResponse<List<MediaPlayerItem>>> GetMediaPlayerListAsync(
        [PathQuery] string secret,
        [PathQuery] string schema,
        [PathQuery] string vhost,
        [PathQuery] string app,
        [PathQuery] string stream);

    /// <summary>截图：**返回 JPEG 字节流**（不是 JSON），失败时 body 为 JSON 错误</summary>
    [HttpGet("/index/api/getSnap")]
    Task<byte[]> GetSnapAsync(
        [PathQuery] string secret,
        [PathQuery] string url,
        [PathQuery] int timeout_sec,
        [PathQuery] int expire_sec);

    /// <summary>下载 bin 文件（返回原始字节）</summary>
    [HttpGet("/index/api/downloadBin")]
    Task<byte[]> DownloadBinAsync([PathQuery] string secret, [PathQuery] string path);

    /// <summary>暂停 RTP 收流超时检测（回放暂停时用）</summary>
    [HttpGet("/index/api/pauseRtpCheck")]
    Task<SimpleResult> PauseRtpCheckAsync([PathQuery] string secret, [PathQuery] string stream_id);

    /// <summary>恢复 RTP 收流超时检测</summary>
    [HttpGet("/index/api/resumeRtpCheck")]
    Task<SimpleResult> ResumeRtpCheckAsync([PathQuery] string secret, [PathQuery] string stream_id);

    /// <summary>
    /// WebRTC SDP 交换：请求体是浏览器 offer（text/plain），返回 {code, id, sdp}。
    /// 注意：该接口不走 secret 鉴权，播放鉴权由 on_play（schema=rtc）负责。
    /// </summary>
    [HttpPost("/index/api/webrtc")]
    Task<WebRtcAnswer> WebRtcAsync(
        [PathQuery] string app,
        [PathQuery] string stream,
        [PathQuery] string type,
        [RawStringContent("text/plain")] string sdp);
}
