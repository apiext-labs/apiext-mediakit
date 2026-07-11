using System.Globalization;
using System.Text.Json;
using Apiext.MediaKit.Domain;
using Apiext.MediaKit.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Volo.Abp;

namespace Apiext.MediaKit;

public sealed class ZLMediaKitClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PlayUrlOptions _playUrl;

    public ZLMediaKitClient(IHttpClientFactory httpClientFactory, IOptions<MediaKitOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _playUrl = options.Value.PlayUrl ?? new PlayUrlOptions();
    }

    public async Task<JsonElement> QueryAsync(MediaServer server, string path,
        IEnumerable<KeyValuePair<string, string?>>? parameters = null, CancellationToken cancellationToken = default)
    {
        var query = parameters?.Where(x => x.Value is not null).ToList() ?? [];
        // Secret 只在服务端拼入 ZLM 请求，绝不返回给浏览器端 DTO。
        query.Add(new("secret", server.Secret));
        var url = QueryHelpers.AddQueryString($"{server.BaseUrl}{path}", query!);

        using var response = await _httpClientFactory.CreateClient("ZLMediaKit").GetAsync(url, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new UserFriendlyException($"ZLMediaKit returned HTTP {(int)response.StatusCode}: {payload}");

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
        {
            var message = root.TryGetProperty("msg", out var msg) ? msg.GetString() : "Unknown ZLMediaKit error";
            throw new UserFriendlyException(message ?? "Unknown ZLMediaKit error");
        }
        return root.Clone();
    }

    public async Task<List<MediaStreamDto>> GetMediaListAsync(MediaServer server, CancellationToken cancellationToken)
    {
        var root = await QueryAsync(server, "/index/api/getMediaList", cancellationToken: cancellationToken);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(item => new MediaStreamDto
        {
            Schema = String(item, "schema"), Vhost = String(item, "vhost"), App = String(item, "app"),
            Stream = String(item, "stream"), ReaderCount = Int(item, "readerCount"),
            TotalReaderCount = Long(item, "totalReaderCount"), BytesSpeed = Long(item, "bytesSpeed"),
            CreateStamp = Long(item, "createStamp"), IsRecordingHls = Bool(item, "isRecordingHLS"),
            IsRecordingMp4 = Bool(item, "isRecordingMP4"), OriginTypeStr = String(item, "originTypeStr")
        }).ToList();
    }

    public async Task<string?> AddStreamProxyAsync(MediaServer server, CreatePullProxyRequest request,
        ProtocolPreset? preset, CancellationToken cancellationToken)
    {
        // ZLM 使用扁平查询参数；这里按“预设优先、页面显式录制选项兜底”展开协议配置。
        var root = await QueryAsync(server, "/index/api/addStreamProxy", Params(
            ("vhost", request.Vhost), ("app", request.App), ("stream", request.Stream),
            ("url", PrimaryUrl(request)), ("retry_count", request.RetryCount.ToString(CultureInfo.InvariantCulture)),
            ("timeout_sec", request.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)),
            ("rtp_type", request.RtpType.ToString(CultureInfo.InvariantCulture)),
            ("enable_hls", Flag(preset?.EnableHls ?? request.EnableHls)),
            ("enable_mp4", Flag(preset?.EnableMp4 ?? request.EnableMp4)),
            ("enable_audio", Flag(preset?.EnableAudio ?? true)),
            ("add_mute_audio", Flag(preset?.AddMuteAudio ?? false)),
            ("enable_rtsp", Flag(preset?.EnableRtsp ?? true)),
            ("enable_rtmp", Flag(preset?.EnableRtmp ?? true)),
            ("enable_ts", Flag(preset?.EnableTs ?? false)),
            ("enable_fmp4", Flag(preset?.EnableFmp4 ?? false)),
            ("auto_close", Flag(preset?.AutoClose ?? false)),
            ("mp4_max_second", preset?.Mp4MaxSeconds.ToString(CultureInfo.InvariantCulture)),
            ("mp4_save_path", preset?.Mp4SavePath), ("hls_save_path", preset?.HlsSavePath)), cancellationToken);
        return root.TryGetProperty("data", out var data) && data.TryGetProperty("key", out var key) ? key.GetString() : null;
    }

    public Task DeleteStreamProxyAsync(MediaServer server, string key, CancellationToken cancellationToken)
        => QueryVoidAsync(server, "/index/api/delStreamProxy", Params(("key", key)), cancellationToken);

    public Task CloseStreamAsync(MediaServer server, string schema, string vhost, string app, string stream, CancellationToken cancellationToken)
        => QueryVoidAsync(server, "/index/api/close_streams", Params(("schema", schema), ("vhost", vhost), ("app", app), ("stream", stream), ("force", "1")), cancellationToken);

    public Task StartRecordAsync(MediaServer server, RecordOperationRequest request, CancellationToken cancellationToken)
        => QueryVoidAsync(server, "/index/api/startRecord", RecordParams(request), cancellationToken);

    public Task StopRecordAsync(MediaServer server, RecordOperationRequest request, CancellationToken cancellationToken)
        => QueryVoidAsync(server, "/index/api/stopRecord", RecordParams(request), cancellationToken);

    public async Task<RecordSearchResultDto> GetRecordFilesAsync(MediaServer server, string vhost, string app,
        string stream, string period, CancellationToken cancellationToken)
    {
        var root = await QueryAsync(server, "/index/api/getMp4RecordFile",
            Params(("vhost", vhost), ("app", app), ("stream", stream), ("period", period)), cancellationToken);
        if (!root.TryGetProperty("data", out var data)) return new(string.Empty, [], []);
        var rootPath = data.TryGetProperty("rootPath", out var rootPathValue) ? rootPathValue.ToString() : string.Empty;
        var paths = data.TryGetProperty("paths", out var pathsValue) && pathsValue.ValueKind == JsonValueKind.Array
            ? pathsValue.EnumerateArray().Select(x => x.ToString()).ToList() : [];
        var files = paths.Select(path =>
        {
            var full = string.IsNullOrWhiteSpace(rootPath) ? path
                : path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) ? path : CombinePath(rootPath, path);
            var name = path.Replace('\\', '/').Split('/').LastOrDefault() ?? path;
            return new RecordFileDto
            {
                FileName = name,
                FilePath = full,
                PlayUrl = BuildDownloadUrl(server, full),
                DownloadUrl = BuildDownloadUrl(server, full)
            };
        }).ToList();
        return new(rootPath, paths, files);
    }

    public StreamPlayInfoDto BuildPlayInfo(MediaServer server, string vhost, string app, string stream)
    {
        var baseUri = new Uri(server.BaseUrl.TrimEnd('/') + "/");
        var host = string.IsNullOrWhiteSpace(_playUrl.PublicHost) ? baseUri.Host : _playUrl.PublicHost.Trim();
        var useHttps = _playUrl.ForceHttps || baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        var httpScheme = useHttps ? "https" : "http";
        var wsScheme = useHttps ? "wss" : "ws";

        var httpPort = useHttps
            ? (_playUrl.HttpsPort ?? (baseUri.IsDefaultPort ? 443 : baseUri.Port))
            : (_playUrl.HttpPort ?? (baseUri.IsDefaultPort ? 80 : baseUri.Port));
        var httpAuthority = IsDefaultPort(httpScheme, httpPort) ? host : $"{host}:{httpPort}";
        var http = $"{httpScheme}://{httpAuthority}";

        var appPath = BuildAppPath(vhost, app, stream);
        var rtspPort = _playUrl.RtspPort <= 0 ? 554 : _playUrl.RtspPort;
        var rtmpPort = _playUrl.RtmpPort <= 0 ? 1935 : _playUrl.RtmpPort;
        var rtspAuthority = rtspPort == 554 ? host : $"{host}:{rtspPort}";
        var rtmpAuthority = rtmpPort == 1935 ? host : $"{host}:{rtmpPort}";

        return new StreamPlayInfoDto
        {
            FlvUrl = $"{http}/{appPath}.live.flv",
            HlsUrl = $"{http}/{appPath}/hls.m3u8",
            WsFlvUrl = $"{wsScheme}://{httpAuthority}/{appPath}.live.flv",
            Fmp4Url = $"{http}/{appPath}.live.mp4",
            RtmpUrl = $"rtmp://{rtmpAuthority}/{appPath}",
            RtspUrl = $"rtsp://{rtspAuthority}/{appPath}",
            SnapUrl = QueryHelpers.AddQueryString($"{http}/index/api/getSnap", new Dictionary<string, string?>
            {
                ["url"] = $"{http}/{appPath}.live.flv",
                ["timeout_sec"] = "10",
                ["expire_sec"] = "30",
                ["secret"] = server.Secret
            })!
        };
    }

    private string BuildAppPath(string vhost, string app, string stream)
    {
        app = (app ?? string.Empty).Trim().Trim('/');
        stream = (stream ?? string.Empty).Trim().Trim('/');
        var isDefaultVhost = string.IsNullOrWhiteSpace(vhost)
            || vhost.Equals("__defaultVhost__", StringComparison.OrdinalIgnoreCase)
            || vhost.Equals("default", StringComparison.OrdinalIgnoreCase);
        if (_playUrl.IncludeCustomVhostInPath && !isDefaultVhost)
            return $"{vhost.Trim().Trim('/')}/{app}/{stream}";
        return $"{app}/{stream}";
    }

    private static bool IsDefaultPort(string scheme, int port)
        => (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && port is 80)
           || (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && port is 443);

    public async Task<List<StreamViewerDto>> GetViewersAsync(MediaServer server, string schema, string vhost,
        string app, string stream, CancellationToken cancellationToken)
    {
        // Prefer dedicated player list API; fall back to session list filtered by identifier.
        try
        {
            var root = await QueryAsync(server, "/index/api/getMediaPlayerList",
                Params(("schema", schema), ("vhost", vhost), ("app", app), ("stream", stream)), cancellationToken);
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                return data.EnumerateArray().Select(item => new StreamViewerDto
                {
                    Id = String(item, "id"),
                    PeerIp = String(item, "peer_ip"),
                    PeerPort = Int(item, "peer_port"),
                    LocalIp = String(item, "local_ip"),
                    LocalPort = Int(item, "local_port"),
                    TypeId = String(item, "typeid"),
                    Identifier = String(item, "identifier")
                }).Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToList();
            }
        }
        catch (UserFriendlyException)
        {
            // older ZLM builds may not expose getMediaPlayerList
        }

        var sessions = await GetSessionsAsync(server, cancellationToken);
        var marker = $"{app}/{stream}";
        return sessions
            .Where(x => (x.Identifier?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false)
                        || x.TypeId.Contains("player", StringComparison.OrdinalIgnoreCase)
                        || x.TypeId.Contains("Player", StringComparison.OrdinalIgnoreCase))
            .Select(x => new StreamViewerDto
            {
                Id = x.Id, PeerIp = x.PeerIp, PeerPort = x.PeerPort,
                LocalIp = x.LocalIp, LocalPort = x.LocalPort, TypeId = x.TypeId, Identifier = x.Identifier
            }).ToList();
    }

    public string BuildDownloadUrl(MediaServer server, string filePath)
    {
        var baseUri = server.BaseUrl.TrimEnd('/');
        return QueryHelpers.AddQueryString($"{baseUri}/index/api/downloadFile", new Dictionary<string, string?>
        {
            ["file_path"] = filePath,
            ["secret"] = server.Secret
        })!;
    }

    public async Task<Dictionary<string, string>> GetConfigurationAsync(MediaServer server, CancellationToken cancellationToken)
    {
        var root = await QueryAsync(server, "/index/api/getServerConfig", cancellationToken: cancellationToken);
        if (!root.TryGetProperty("data", out var data)) return [];
        var result = new Dictionary<string, string>();
        IEnumerable<JsonElement> objects = data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToArray()
            : new[] { data };
        foreach (var item in objects)
            foreach (var property in item.EnumerateObject()) result[property.Name] = property.Value.ToString();
        return result;
    }

    public Task SetConfigurationAsync(MediaServer server, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        => QueryVoidAsync(server, "/index/api/setServerConfig", values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)), cancellationToken);

    public async Task<JsonElement> GetStatisticAsync(MediaServer server, CancellationToken cancellationToken)
        => await QueryAsync(server, "/index/api/getStatistic", cancellationToken: cancellationToken);

    public async Task<List<NetworkSessionDto>> GetSessionsAsync(MediaServer server, CancellationToken cancellationToken)
    {
        var root = await QueryAsync(server, "/index/api/getAllSession", cancellationToken: cancellationToken);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(item => new NetworkSessionDto
        {
            Id = String(item, "id"), LocalIp = String(item, "local_ip"), LocalPort = Int(item, "local_port"),
            PeerIp = String(item, "peer_ip"), PeerPort = Int(item, "peer_port"), TypeId = String(item, "typeid"),
            Identifier = String(item, "identifier")
        }).ToList();
    }

    public Task KickSessionAsync(MediaServer server, string sessionId, CancellationToken cancellationToken)
        => QueryVoidAsync(server, "/index/api/kick_session", Params(("id", sessionId)), cancellationToken);

    public async Task<string?> AddStreamPusherProxyAsync(MediaServer server, CreatePushProxyRequest request,
        CancellationToken cancellationToken)
    {
        // ZLM accepts schema as rtsp/rtmp only; normalize secure variants.
        var schema = (request.Schema ?? "rtsp").Trim().ToLowerInvariant() switch
        {
            "rtsps" => "rtsp",
            "rtmps" => "rtmp",
            var s when s is "rtsp" or "rtmp" => s,
            _ => "rtsp"
        };
        var root = await QueryAsync(server, "/index/api/addStreamPusherProxy", Params(
            ("schema", schema),
            ("vhost", request.Vhost),
            ("app", request.App),
            ("stream", request.Stream),
            ("dst_url", request.DestinationUrl),
            ("rtp_type", request.RtpType.ToString(CultureInfo.InvariantCulture)),
            ("retry_count", request.RetryCount.ToString(CultureInfo.InvariantCulture)),
            ("timeout_sec", request.TimeoutSeconds.ToString(CultureInfo.InvariantCulture))), cancellationToken);
        return root.TryGetProperty("data", out var data) && data.TryGetProperty("key", out var key)
            ? key.GetString()
            : null;
    }

    public Task DeleteStreamPusherProxyAsync(MediaServer server, string key, CancellationToken cancellationToken)
        => QueryVoidAsync(server, "/index/api/delStreamPusherProxy", Params(("key", key)), cancellationToken);

    public string BuildWhipUrl(MediaServer server, string app, string stream)
    {
        var baseUri = server.BaseUrl.TrimEnd('/');
        return QueryHelpers.AddQueryString($"{baseUri}/index/api/whip", new Dictionary<string, string?>
        {
            ["app"] = app,
            ["stream"] = stream,
            ["secret"] = server.Secret
        })!;
    }

    private async Task QueryVoidAsync(MediaServer server, string path, IEnumerable<KeyValuePair<string, string?>> parameters, CancellationToken cancellationToken)
        => _ = await QueryAsync(server, path, parameters, cancellationToken);

    private static IEnumerable<KeyValuePair<string, string?>> RecordParams(RecordOperationRequest request) => Params(
        ("type", request.Type.ToString(CultureInfo.InvariantCulture)), ("vhost", request.Vhost),
        ("app", request.App), ("stream", request.Stream));
    private static List<KeyValuePair<string, string?>> Params(params (string Key, string? Value)[] values)
        => values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)).ToList();
    private static string Flag(bool value) => value ? "1" : "0";
    private static string String(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;
    private static int Int(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static long Long(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static bool Bool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetInt32() != 0);
    private static string PrimaryUrl(CreatePullProxyRequest request)
        => request.SourceUrls.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? request.SourceUrl;
    private static string CombinePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root)) return relative;
        if (string.IsNullOrWhiteSpace(relative)) return root;
        return root.TrimEnd('/', '\\') + "/" + relative.TrimStart('/', '\\');
    }
}
