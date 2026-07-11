namespace Apiext.MediaKit.Models;

public sealed class MediaServerDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTime CreationTime { get; set; }
    public MediaServerDto() { }
    public MediaServerDto(Guid id, string name, string baseUrl, bool isEnabled, DateTime creationTime)
        => (Id, Name, BaseUrl, IsEnabled, CreationTime) = (id, name, baseUrl, isEnabled, creationTime);
}
public sealed record SaveMediaServerRequest(string Name, string BaseUrl, string Secret, bool IsEnabled = true);

public sealed record DashboardDto(
    int OnlineStreams,
    int TotalReaders,
    int PullProxies,
    int TcpSessions,
    int MediaSources,
    bool ServerReachable,
    string? Error,
    long BytesIn = 0,
    long BytesOut = 0,
    string ServerName = "");

public sealed class MediaStreamDto
{
    public string Schema { get; set; } = string.Empty;
    public string Vhost { get; set; } = string.Empty;
    public string App { get; set; } = string.Empty;
    public string Stream { get; set; } = string.Empty;
    public int ReaderCount { get; set; }
    public long TotalReaderCount { get; set; }
    public long BytesSpeed { get; set; }
    public long CreateStamp { get; set; }
    public bool IsRecordingHls { get; set; }
    public bool IsRecordingMp4 { get; set; }
    public string OriginTypeStr { get; set; } = string.Empty;
}

public sealed class PullProxyDto
{
    public Guid Id { get; set; }
    public Guid MediaServerId { get; set; }
    public string Vhost { get; set; } = string.Empty;
    public string App { get; set; } = string.Empty;
    public string Stream { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public List<string> SourceUrls { get; set; } = [];
    public string Remark { get; set; } = string.Empty;
    public string? ProxyKey { get; set; }
    public bool EnableHls { get; set; }
    public bool EnableMp4 { get; set; }
    public bool IsActive { get; set; }
    public bool OnDemand { get; set; }
    public Guid? ProtocolPresetId { get; set; }
    public int RetryCount { get; set; }
    public int TimeoutSeconds { get; set; }
    public int RtpType { get; set; }
}

public sealed class CreatePullProxyRequest
{
    public Guid MediaServerId { get; set; }
    public string Vhost { get; set; } = "__defaultVhost__";
    public string App { get; set; } = "live";
    public string Stream { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public List<string> SourceUrls { get; set; } = [];
    public string Remark { get; set; } = string.Empty;
    public Guid? ProtocolPresetId { get; set; }
    public bool OnDemand { get; set; }
    public int RetryCount { get; set; } = -1;
    public int TimeoutSeconds { get; set; } = 10;
    public int RtpType { get; set; }
    public bool EnableHls { get; set; } = true;
    public bool EnableMp4 { get; set; }
}

public class ProtocolPresetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool EnableAudio { get; set; } = true;
    public bool AddMuteAudio { get; set; }
    public bool EnableHls { get; set; } = true;
    public bool EnableMp4 { get; set; }
    public bool EnableRtsp { get; set; } = true;
    public bool EnableRtmp { get; set; } = true;
    public bool EnableTs { get; set; }
    public bool EnableFmp4 { get; set; }
    public bool AutoClose { get; set; }
    public int Mp4MaxSeconds { get; set; }
    public string Mp4SavePath { get; set; } = string.Empty;
    public string HlsSavePath { get; set; } = string.Empty;
}

public sealed class SaveProtocolPresetRequest : ProtocolPresetDto { }

public sealed class NetworkSessionDto
{
    public string Id { get; set; } = string.Empty;
    public string LocalIp { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string PeerIp { get; set; } = string.Empty;
    public int PeerPort { get; set; }
    public string TypeId { get; set; } = string.Empty;
    public string? Identifier { get; set; }
}

public sealed class StreamViewerDto
{
    public string Id { get; set; } = string.Empty;
    public string PeerIp { get; set; } = string.Empty;
    public int PeerPort { get; set; }
    public string LocalIp { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string TypeId { get; set; } = string.Empty;
    public string? Identifier { get; set; }
}

public sealed class StreamPlayInfoDto
{
    public string FlvUrl { get; set; } = string.Empty;
    public string HlsUrl { get; set; } = string.Empty;
    public string WsFlvUrl { get; set; } = string.Empty;
    public string Fmp4Url { get; set; } = string.Empty;
    public string RtmpUrl { get; set; } = string.Empty;
    public string RtspUrl { get; set; } = string.Empty;
    public string SnapUrl { get; set; } = string.Empty;
}

public sealed class RecordFileDto
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string PlayUrl { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long StartTime { get; set; }
    public double TimeLen { get; set; }
}

public sealed record RecordOperationRequest(Guid MediaServerId, string Vhost, string App, string Stream, int Type = 1);
public sealed record RecordSearchResultDto(string RootPath, List<string> Paths, List<RecordFileDto> Files);
public sealed class ConfigurationEntryDto
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ConfigurationEntryDto() { }
    public ConfigurationEntryDto(string key, string value) => (Key, Value) = (key, value);
}
public sealed record SaveConfigurationRequest(Guid MediaServerId, Dictionary<string, string> Values);

public sealed class WebhookEventDto
{
    public Guid Id { get; set; }
    public string EventName { get; set; } = string.Empty;
    public Guid? MediaServerId { get; set; }
    public string? Vhost { get; set; }
    public string? App { get; set; }
    public string? Stream { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTime ReceivedAt { get; set; }
}

public sealed class PushProxyDto
{
    public Guid Id { get; set; }
    public Guid MediaServerId { get; set; }
    public string Schema { get; set; } = "rtsp";
    public string Vhost { get; set; } = string.Empty;
    public string App { get; set; } = string.Empty;
    public string Stream { get; set; } = string.Empty;
    public string DestinationUrl { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public string? ProxyKey { get; set; }
    public int RetryCount { get; set; } = -1;
    public int TimeoutSeconds { get; set; } = 10;
    public int RtpType { get; set; }
    public bool IsActive { get; set; }
}

public sealed class CreatePushProxyRequest
{
    public Guid MediaServerId { get; set; }
    public string Schema { get; set; } = "rtsp";
    public string Vhost { get; set; } = "__defaultVhost__";
    public string App { get; set; } = "live";
    public string Stream { get; set; } = string.Empty;
    public string DestinationUrl { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public int RetryCount { get; set; } = -1;
    public int TimeoutSeconds { get; set; } = 10;
    public int RtpType { get; set; }
    public bool StartImmediately { get; set; } = true;
}

public sealed class WhipInfoDto
{
    public string WhipUrl { get; set; } = string.Empty;
    public string App { get; set; } = string.Empty;
    public string Stream { get; set; } = string.Empty;
    public string ServerBaseUrl { get; set; } = string.Empty;
}

public sealed class PluginInfoDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public PluginInfoDto() { }
    public PluginInfoDto(string name, string description, bool enabled)
        => (Name, Description, Enabled) = (name, description, enabled);
}

public sealed record SecurityStatusDto(
    bool WeakAdminPassword,
    bool WebhookSecretConfigured,
    bool StreamAuthEnabled,
    bool StreamAuthKeyIsDefault,
    string EnvironmentName,
    string WebhookUrlHint);

public sealed record IssueStreamTokenRequest(string App, string Stream, string Purpose = "play", int? TtlMinutes = null);

public sealed record StreamTokenDto(
    string Token,
    long ExpiresAtUnix,
    string Purpose,
    string App,
    string Stream,
    int TtlMinutes,
    string QueryPrefix,
    string QueryAppend);

public sealed record HostStatsDto(
    string MachineName,
    string OsDescription,
    int ProcessorCount,
    double ProcessCpuPercent,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    long UptimeSeconds,
    DateTime SampledAtUtc);
