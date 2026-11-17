using System.Text.Json.Serialization;

namespace Apiext.MediaKit.Server.Apis;

/// <summary>ZLM 响应通用契约：让 ZlmService 能统一记录 code/msg，而不必为每个接口写日志。</summary>
public interface IZlmResult
{
    int Code { get; }
    string? Msg { get; }
}

/// <summary>ZLMediaKit 统一响应结构 {code, msg, data}</summary>
public class ZlmResponse<T> : IZlmResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary>在线流（getMediaList 单条）</summary>
public class MediaItem
{
    [JsonPropertyName("app")]
    public string? App { get; set; }

    [JsonPropertyName("stream")]
    public string? Stream { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("vhost")]
    public string? Vhost { get; set; }

    [JsonPropertyName("bytesSpeed")]
    public long BytesSpeed { get; set; }

    [JsonPropertyName("readerCount")]
    public int ReaderCount { get; set; }

    [JsonPropertyName("totalReaderCount")]
    public int TotalReaderCount { get; set; }

    [JsonPropertyName("aliveSecond")]
    public long AliveSecond { get; set; }

    [JsonPropertyName("originTypeStr")]
    public string? OriginTypeStr { get; set; }

    [JsonPropertyName("originUrl")]
    public string? OriginUrl { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("isRecordingHLS")]
    public bool IsRecordingHLS { get; set; }

    [JsonPropertyName("isRecordingMP4")]
    public bool IsRecordingMP4 { get; set; }

    [JsonPropertyName("tracks")]
    public List<MediaTrack>? Tracks { get; set; }
}

public class MediaTrack
{
    // 注意：ZLM 的字段名是下划线风格，且 codec_type 是数字（0 视频 / 1 音频）
    [JsonPropertyName("codec_id")]
    public int CodecId { get; set; }

    [JsonPropertyName("codec_id_name")]
    public string? CodecIdName { get; set; }

    [JsonPropertyName("codec_type")]
    public int CodecType { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    /// <summary>
    /// 帧率。ZLM 返回的是浮点数（25.0 / 29.97 / 0.0），声明成 int 会让整个 getMediaList
    /// 反序列化抛 JsonException（fps 超界），导致在线流列表/监控页拿不到数据。
    /// </summary>
    [JsonPropertyName("fps")]
    public double Fps { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    // 音频轨道专有字段
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; }

    [JsonPropertyName("channels")]
    public int Channels { get; set; }
}

/// <summary>服务器统计（getStatistic）</summary>
public class StatisticData
{
    [JsonPropertyName("MediaSource")]
    public int MediaSource { get; set; }

    [JsonPropertyName("MultiMediaSourceMuxer")]
    public int MultiMediaSourceMuxer { get; set; }

    [JsonPropertyName("TcpServer")]
    public int TcpServer { get; set; }

    [JsonPropertyName("TcpSession")]
    public int TcpSession { get; set; }

    [JsonPropertyName("UdpServer")]
    public int UdpServer { get; set; }

    [JsonPropertyName("UdpSession")]
    public int UdpSession { get; set; }

    [JsonPropertyName("TcpClient")]
    public int TcpClient { get; set; }

    [JsonPropertyName("Socket")]
    public int Socket { get; set; }
}

/// <summary>setServerConfig 返回结果</summary>
public class SetServerConfigResult : IZlmResult
{
    [JsonPropertyName("changed")]
    public int Changed { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }
}

/// <summary>拉流代理源（vhost/app/stream）</summary>
public class PullProxySrc
{
    [JsonPropertyName("vhost")]
    public string? Vhost { get; set; }

    [JsonPropertyName("app")]
    public string? App { get; set; }

    [JsonPropertyName("stream")]
    public string? Stream { get; set; }
}

/// <summary>拉流代理（listStreamProxy 单条）</summary>
public class PullProxyItem
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("src")]
    public PullProxySrc? Src { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("enable_audio")]
    public int EnableAudio { get; set; }

    [JsonPropertyName("add_mute_audio")]
    public int AddMuteAudio { get; set; }

    [JsonPropertyName("rePullCount")]
    public int RePullCount { get; set; }

    [JsonPropertyName("totalReaderCount")]
    public int TotalReaderCount { get; set; }

    [JsonPropertyName("isOn")]
    public bool IsOn { get; set; }

    [JsonPropertyName("aliveSecond")]
    public long AliveSecond { get; set; }
}

/// <summary>addStreamProxy 返回 data</summary>
public class AddStreamProxyData
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }
}

/// <summary>addFFmpegSource 返回（key 即 dst_url，用于 delFFmpegSource）</summary>
public class AddFFmpegSourceData
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("chid")]
    public long Chid { get; set; }
}

/// <summary>线程负载（getThreadsLoad / getWorkThreadsLoad 单条）</summary>
public class ThreadLoadItem
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("load")]
    public int Load { get; set; }

    [JsonPropertyName("fd_count")]
    public int FdCount { get; set; }

    [JsonPropertyName("delay")]
    public int Delay { get; set; }
}

/// <summary>在线会话（getAllSession 单条，含播放器/推流器等）</summary>
public class SessionItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("peer_ip")]
    public string? PeerIp { get; set; }

    [JsonPropertyName("peer_port")]
    public int PeerPort { get; set; }

    [JsonPropertyName("local_ip")]
    public string? LocalIp { get; set; }

    [JsonPropertyName("local_port")]
    public int LocalPort { get; set; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("typeid")]
    public string? TypeId { get; set; }
}

/// <summary>openRtpServer 返回：port 在顶层（{code, msg, port}）</summary>
public class OpenRtpServerResult : IZlmResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

/// <summary>closeRtpServer 返回：hit 在顶层（{code, msg, hit}）</summary>
public class CloseRtpServerResult : IZlmResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("hit")]
    public int Hit { get; set; }
}

/// <summary>getRtpInfo 返回（data 内的 RTP 收流状态）</summary>
public class RtpInfo
{
    [JsonPropertyName("exist")]
    public bool Exist { get; set; }

    [JsonPropertyName("peer_ip")]
    public string? PeerIp { get; set; }

    [JsonPropertyName("peer_port")]
    public int PeerPort { get; set; }

    [JsonPropertyName("local_ip")]
    public string? LocalIp { get; set; }

    [JsonPropertyName("local_port")]
    public int LocalPort { get; set; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }
}

/// <summary>startRecord/stopRecord 返回（result 在顶层）</summary>
public class RecordResult : IZlmResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("result")]
    public bool Result { get; set; }
}

/// <summary>isRecording 返回（status 在顶层）</summary>
public class IsRecordingResult : IZlmResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("status")]
    public bool Status { get; set; }
}

/// <summary>getMp4RecordFile 返回的 data（录像文件列表；period 为完整日期时 paths 是文件，不完整时是日期目录）</summary>
public class Mp4RecordList
{
    [JsonPropertyName("paths")]
    public List<string>? Paths { get; set; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; set; }
}

// ================= 以下按官方 RESTful 接口全量补全（业务暂未全部使用） =================

/// <summary>只有 code/msg 的通用返回（restartServer / kick_session / pauseRtpCheck / resumeRtpCheck）</summary>
public class SimpleResult : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
}

/// <summary>version 返回</summary>
public class VersionResult : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("data")] public VersionData? Data { get; set; }
}

public class VersionData
{
    [JsonPropertyName("branchName")] public string? BranchName { get; set; }
    [JsonPropertyName("buildTime")] public string? BuildTime { get; set; }
    [JsonPropertyName("commitHash")] public string? CommitHash { get; set; }
}

/// <summary>close_stream 返回：result 0 成功 / -1 失败 / -2 流不存在</summary>
public class CloseStreamResult : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("result")] public int? Result { get; set; }
}

/// <summary>close_streams 返回</summary>
public class CloseStreamsResult : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("count_hit")] public int CountHit { get; set; }
    [JsonPropertyName("count_closed")] public int CountClosed { get; set; }
}

/// <summary>kick_sessions 返回</summary>
public class KickSessionsResult : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("count_hit")] public int CountHit { get; set; }
}

/// <summary>isMediaOnline 返回（已过期接口）</summary>
public class IsMediaOnlineResult : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
}

/// <summary>getMediaInfo 返回（已过期接口，字段与 getMediaList 单条基本一致）</summary>
public class MediaInfoResult : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("readerCount")] public int ReaderCount { get; set; }
    [JsonPropertyName("totalReaderCount")] public int TotalReaderCount { get; set; }
    [JsonPropertyName("tracks")] public List<MediaTrack>? Tracks { get; set; }
}

/// <summary>listRtpServer 单条</summary>
public class RtpServerItem
{
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("stream_id")] public string? StreamId { get; set; }
}

/// <summary>startSendRtpPassive 返回：local_port 为本地监听端口</summary>
public class StartSendRtpPassiveResult : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("local_port")] public int LocalPort { get; set; }
}

/// <summary>addStreamPusherProxy 返回：key 用于 delStreamPusherProxy</summary>
public class AddStreamPusherProxyData
{
    [JsonPropertyName("key")] public string? Key { get; set; }
}

/// <summary>getMediaPlayerList 单条（typeid 是字符串，如 mediakit::RtspSession）</summary>
public class MediaPlayerItem
{
    [JsonPropertyName("identifier")] public string? Identifier { get; set; }
    [JsonPropertyName("local_ip")] public string? LocalIp { get; set; }
    [JsonPropertyName("local_port")] public int LocalPort { get; set; }
    [JsonPropertyName("peer_ip")] public string? PeerIp { get; set; }
    [JsonPropertyName("peer_port")] public int PeerPort { get; set; }
    [JsonPropertyName("typeid")] public string? TypeId { get; set; }
}

/// <summary>webrtc SDP 交换返回：成功时 code=0 + id + sdp（answer）</summary>
public class WebRtcAnswer : IZlmResult
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("sdp")] public string? Sdp { get; set; }
}
