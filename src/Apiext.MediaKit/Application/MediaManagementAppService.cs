using System.Text.Json;
using Apiext.MediaKit.Domain;
using Apiext.MediaKit.Models;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace Apiext.MediaKit;

public class MediaManagementAppService : ApplicationService
{
    private readonly IRepository<MediaServer, Guid> _servers;
    private readonly IRepository<PullProxy, Guid> _proxies;
    private readonly IRepository<PushProxy, Guid> _pushProxies;
    private readonly IRepository<WebhookEvent, Guid> _webhooks;
    private readonly IRepository<ProtocolPreset, Guid> _presets;
    private readonly ZLMediaKitClient _client;

    public MediaManagementAppService(IRepository<MediaServer, Guid> servers,
        IRepository<PullProxy, Guid> proxies, IRepository<PushProxy, Guid> pushProxies,
        IRepository<WebhookEvent, Guid> webhooks, IRepository<ProtocolPreset, Guid> presets,
        ZLMediaKitClient client)
    {
        _servers = servers;
        _proxies = proxies;
        _pushProxies = pushProxies;
        _webhooks = webhooks;
        _presets = presets;
        _client = client;
    }

    public virtual async Task<List<MediaServerDto>> GetServersAsync(CancellationToken cancellationToken = default)
        => (await _servers.GetListAsync(cancellationToken: cancellationToken))
            .OrderBy(x => x.Name)
            .Select(x => new MediaServerDto(x.Id, x.Name, x.BaseUrl, x.IsEnabled, x.CreationTime)).ToList();

    public virtual async Task<MediaServerDto> CreateServerAsync(SaveMediaServerRequest request, CancellationToken cancellationToken = default)
    {
        ValidateServer(request);
        var entity = new MediaServer(GuidGenerator.Create(), request.Name, request.BaseUrl, request.Secret, request.IsEnabled);
        await _servers.InsertAsync(entity, autoSave: true, cancellationToken);
        return new(entity.Id, entity.Name, entity.BaseUrl, entity.IsEnabled, entity.CreationTime);
    }

    public virtual async Task<MediaServerDto> UpdateServerAsync(Guid id, SaveMediaServerRequest request, CancellationToken cancellationToken = default)
    {
        ValidateServer(request);
        var entity = await _servers.GetAsync(id, cancellationToken: cancellationToken);
        entity.Update(request.Name, request.BaseUrl, request.Secret, request.IsEnabled);
        await _servers.UpdateAsync(entity, autoSave: true, cancellationToken);
        return new(entity.Id, entity.Name, entity.BaseUrl, entity.IsEnabled, entity.CreationTime);
    }

    public virtual Task DeleteServerAsync(Guid id, CancellationToken cancellationToken = default)
        => _servers.DeleteAsync(id, autoSave: true, cancellationToken);

    public virtual async Task<DashboardDto> GetDashboardAsync(Guid? serverId, CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var proxyCount = await _proxies.CountAsync(x => x.MediaServerId == server.Id && x.IsActive, cancellationToken);
        try
        {
            var streams = await _client.GetMediaListAsync(server, cancellationToken);
            var statistic = await _client.GetStatisticAsync(server, cancellationToken);
            var data = statistic.TryGetProperty("data", out var value) ? value : default;
            return new(streams.Count, streams.Sum(x => x.ReaderCount), (int)proxyCount,
                Int(data, "TcpSession"), Int(data, "MediaSource"), true, null,
                Long(data, "BytesIn") + Long(data, "Buffer"), Long(data, "BytesOut"), server.Name);
        }
        catch (Exception exception)
        {
            return new(0, 0, (int)proxyCount, 0, 0, false, exception.Message, 0, 0, server.Name);
        }
    }

    public virtual async Task<List<MediaStreamDto>> GetMediaAsync(Guid? serverId, CancellationToken cancellationToken = default)
        => await _client.GetMediaListAsync(await GetServerAsync(serverId, cancellationToken), cancellationToken);

    public virtual async Task CloseMediaAsync(Guid? serverId, string schema, string vhost, string app, string stream, CancellationToken cancellationToken = default)
        => await _client.CloseStreamAsync(await GetServerAsync(serverId, cancellationToken), schema, vhost, app, stream, cancellationToken);

    public virtual async Task<StreamPlayInfoDto> GetPlayInfoAsync(Guid? serverId, string vhost, string app, string stream,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        return _client.BuildPlayInfo(server, vhost, app, stream);
    }

    public virtual async Task<List<StreamViewerDto>> GetViewersAsync(Guid? serverId, string schema, string vhost,
        string app, string stream, CancellationToken cancellationToken = default)
        => await _client.GetViewersAsync(await GetServerAsync(serverId, cancellationToken), schema, vhost, app, stream, cancellationToken);

    public virtual async Task<List<PullProxyDto>> GetPullProxiesAsync(Guid? serverId, CancellationToken cancellationToken = default)
    {
        var query = await _proxies.GetQueryableAsync();
        if (serverId.HasValue) query = query.Where(x => x.MediaServerId == serverId);
        var entities = await query.OrderByDescending(x => x.CreationTime).ToListAsync(cancellationToken);
        return entities.Select(MapProxy).ToList();
    }

    public virtual async Task<PullProxyDto> CreatePullProxyAsync(CreatePullProxyRequest request, CancellationToken cancellationToken = default)
    {
        var sourceUrls = request.SourceUrls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (sourceUrls.Count == 0 && !string.IsNullOrWhiteSpace(request.SourceUrl)) sourceUrls.Add(request.SourceUrl);
        if (string.IsNullOrWhiteSpace(request.Stream) || sourceUrls.Count == 0)
            throw new UserFriendlyException("Stream and at least one source URL are required.");
        var server = await GetServerAsync(request.MediaServerId, cancellationToken);
        var preset = request.ProtocolPresetId.HasValue
            ? await _presets.FindAsync(request.ProtocolPresetId.Value, cancellationToken: cancellationToken)
            : null;
        var proxy = new PullProxy(GuidGenerator.Create(), server.Id, request.Vhost, request.App,
            request.Stream, sourceUrls, request.Remark, request.ProtocolPresetId, request.OnDemand,
            request.RetryCount, request.TimeoutSeconds, request.RtpType, request.EnableHls, request.EnableMp4);
        if (!request.OnDemand)
        {
            var key = await _client.AddStreamProxyAsync(server, request, preset, cancellationToken);
            proxy.MarkStarted(key);
        }
        await _proxies.InsertAsync(proxy, autoSave: true, cancellationToken);
        return (await GetPullProxiesAsync(server.Id, cancellationToken)).Single(x => x.Id == proxy.Id);
    }

    public virtual async Task StopPullProxyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var proxy = await _proxies.GetAsync(id, cancellationToken: cancellationToken);
        var server = await _servers.GetAsync(proxy.MediaServerId, cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(proxy.ProxyKey))
            await _client.DeleteStreamProxyAsync(server, proxy.ProxyKey, cancellationToken);
        proxy.MarkStopped();
        await _proxies.UpdateAsync(proxy, autoSave: true, cancellationToken);
    }

    public virtual async Task DeletePullProxyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var proxy = await _proxies.GetAsync(id, cancellationToken: cancellationToken);
        if (proxy.IsActive && !string.IsNullOrWhiteSpace(proxy.ProxyKey))
        {
            var server = await _servers.GetAsync(proxy.MediaServerId, cancellationToken: cancellationToken);
            await _client.DeleteStreamProxyAsync(server, proxy.ProxyKey, cancellationToken);
        }
        await _proxies.DeleteAsync(proxy, autoSave: true, cancellationToken);
    }

    public virtual async Task TogglePullProxyModeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var proxy = await _proxies.GetAsync(id, cancellationToken: cancellationToken);
        var server = await _servers.GetAsync(proxy.MediaServerId, cancellationToken: cancellationToken);
        if (proxy.OnDemand)
        {
            var request = ToCreateRequest(proxy);
            var preset = proxy.ProtocolPresetId.HasValue
                ? await _presets.FindAsync(proxy.ProtocolPresetId.Value, cancellationToken: cancellationToken)
                : null;
            var key = await _client.AddStreamProxyAsync(server, request, preset, cancellationToken);
            proxy.SetMode(false);
            proxy.MarkStarted(key);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(proxy.ProxyKey))
                await _client.DeleteStreamProxyAsync(server, proxy.ProxyKey, cancellationToken);
            proxy.SetMode(true);
            proxy.MarkStopped();
        }
        await _proxies.UpdateAsync(proxy, autoSave: true, cancellationToken);
    }

    public virtual async Task<PullProxyDto> UpdatePullProxyAsync(Guid id, CreatePullProxyRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceUrls = request.SourceUrls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (sourceUrls.Count == 0 && !string.IsNullOrWhiteSpace(request.SourceUrl)) sourceUrls.Add(request.SourceUrl);
        if (sourceUrls.Count == 0) throw new UserFriendlyException("At least one source URL is required.");

        var proxy = await _proxies.GetAsync(id, cancellationToken: cancellationToken);
        var server = await _servers.GetAsync(proxy.MediaServerId, cancellationToken: cancellationToken);
        var wasActive = proxy.IsActive && !string.IsNullOrWhiteSpace(proxy.ProxyKey);
        if (wasActive)
            await _client.DeleteStreamProxyAsync(server, proxy.ProxyKey!, cancellationToken);

        proxy.UpdateConfig(sourceUrls, request.Remark, request.ProtocolPresetId, request.OnDemand,
            request.RetryCount, request.TimeoutSeconds, request.RtpType, request.EnableHls, request.EnableMp4);

        if (!request.OnDemand)
        {
            var preset = request.ProtocolPresetId.HasValue
                ? await _presets.FindAsync(request.ProtocolPresetId.Value, cancellationToken: cancellationToken)
                : null;
            var key = await _client.AddStreamProxyAsync(server, ToCreateRequest(proxy), preset, cancellationToken);
            proxy.MarkStarted(key);
        }
        else
        {
            proxy.MarkStopped();
        }

        await _proxies.UpdateAsync(proxy, autoSave: true, cancellationToken);
        return MapProxy(proxy);
    }

    public virtual async Task<List<ProtocolPresetDto>> GetProtocolPresetsAsync(CancellationToken cancellationToken = default)
        => (await _presets.GetListAsync(cancellationToken: cancellationToken)).OrderBy(x => x.Name).Select(MapPreset).ToList();

    public virtual async Task<ProtocolPresetDto> SaveProtocolPresetAsync(Guid? id, SaveProtocolPresetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new UserFriendlyException("Preset name is required.");
        var entity = id.HasValue
            ? await _presets.GetAsync(id.Value, cancellationToken: cancellationToken)
            : new ProtocolPreset(GuidGenerator.Create(), request.Name);
        entity.Update(request.Name, request.EnableAudio, request.AddMuteAudio, request.EnableHls,
            request.EnableMp4, request.EnableRtsp, request.EnableRtmp, request.EnableTs,
            request.EnableFmp4, request.AutoClose, request.Mp4MaxSeconds,
            request.Mp4SavePath, request.HlsSavePath);
        if (id.HasValue) await _presets.UpdateAsync(entity, autoSave: true, cancellationToken);
        else await _presets.InsertAsync(entity, autoSave: true, cancellationToken);
        return MapPreset(entity);
    }

    public virtual Task DeleteProtocolPresetAsync(Guid id, CancellationToken cancellationToken = default)
        => _presets.DeleteAsync(id, autoSave: true, cancellationToken);

    public virtual async Task<List<NetworkSessionDto>> GetSessionsAsync(Guid? serverId, CancellationToken cancellationToken = default)
        => await _client.GetSessionsAsync(await GetServerAsync(serverId, cancellationToken), cancellationToken);

    public virtual async Task KickSessionAsync(Guid? serverId, string sessionId, CancellationToken cancellationToken = default)
        => await _client.KickSessionAsync(await GetServerAsync(serverId, cancellationToken), sessionId, cancellationToken);

    public virtual async Task StartRecordAsync(RecordOperationRequest request, CancellationToken cancellationToken = default)
        => await _client.StartRecordAsync(await GetServerAsync(request.MediaServerId, cancellationToken), request, cancellationToken);

    public virtual async Task StopRecordAsync(RecordOperationRequest request, CancellationToken cancellationToken = default)
        => await _client.StopRecordAsync(await GetServerAsync(request.MediaServerId, cancellationToken), request, cancellationToken);

    public virtual async Task<RecordSearchResultDto> GetRecordFilesAsync(Guid? serverId, string vhost, string app,
        string stream, string period, CancellationToken cancellationToken = default)
        => await _client.GetRecordFilesAsync(await GetServerAsync(serverId, cancellationToken), vhost, app, stream, period, cancellationToken);

    public virtual async Task<List<ConfigurationEntryDto>> GetConfigurationAsync(Guid? serverId, CancellationToken cancellationToken = default)
        => (await _client.GetConfigurationAsync(await GetServerAsync(serverId, cancellationToken), cancellationToken))
            .OrderBy(x => x.Key).Select(x => new ConfigurationEntryDto(x.Key, x.Value)).ToList();

    public virtual async Task SaveConfigurationAsync(SaveConfigurationRequest request, CancellationToken cancellationToken = default)
        => await _client.SetConfigurationAsync(await GetServerAsync(request.MediaServerId, cancellationToken), request.Values, cancellationToken);

    public virtual async Task<WhipInfoDto> GetWhipInfoAsync(Guid? serverId, string app, string stream,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(stream))
            throw new UserFriendlyException("App and stream are required.");
        var server = await GetServerAsync(serverId, cancellationToken);
        return new WhipInfoDto
        {
            App = app.Trim(),
            Stream = stream.Trim(),
            ServerBaseUrl = server.BaseUrl,
            WhipUrl = _client.BuildWhipUrl(server, app.Trim(), stream.Trim())
        };
    }

    public virtual async Task<List<PushProxyDto>> GetPushProxiesAsync(Guid? serverId, CancellationToken cancellationToken = default)
    {
        var query = await _pushProxies.GetQueryableAsync();
        if (serverId.HasValue) query = query.Where(x => x.MediaServerId == serverId);
        var entities = await query.OrderByDescending(x => x.CreationTime).ToListAsync(cancellationToken);
        return entities.Select(MapPushProxy).ToList();
    }

    public virtual async Task<PushProxyDto> CreatePushProxyAsync(CreatePushProxyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Stream) || string.IsNullOrWhiteSpace(request.DestinationUrl))
            throw new UserFriendlyException("Stream and destination URL are required.");
        var server = await GetServerAsync(request.MediaServerId, cancellationToken);
        var entity = new PushProxy(GuidGenerator.Create(), server.Id, request.Schema, request.Vhost,
            request.App, request.Stream, request.DestinationUrl, request.Remark,
            request.RetryCount, request.TimeoutSeconds, request.RtpType);
        if (request.StartImmediately)
        {
            var key = await _client.AddStreamPusherProxyAsync(server, request, cancellationToken);
            entity.MarkStarted(key);
        }
        await _pushProxies.InsertAsync(entity, autoSave: true, cancellationToken);
        return MapPushProxy(entity);
    }

    public virtual async Task StartPushProxyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _pushProxies.GetAsync(id, cancellationToken: cancellationToken);
        var server = await _servers.GetAsync(entity.MediaServerId, cancellationToken: cancellationToken);
        if (entity.IsActive && !string.IsNullOrWhiteSpace(entity.ProxyKey)) return;
        var key = await _client.AddStreamPusherProxyAsync(server, ToPushRequest(entity), cancellationToken);
        entity.MarkStarted(key);
        await _pushProxies.UpdateAsync(entity, autoSave: true, cancellationToken);
    }

    public virtual async Task StopPushProxyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _pushProxies.GetAsync(id, cancellationToken: cancellationToken);
        var server = await _servers.GetAsync(entity.MediaServerId, cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(entity.ProxyKey))
            await _client.DeleteStreamPusherProxyAsync(server, entity.ProxyKey, cancellationToken);
        entity.MarkStopped();
        await _pushProxies.UpdateAsync(entity, autoSave: true, cancellationToken);
    }

    public virtual async Task DeletePushProxyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _pushProxies.GetAsync(id, cancellationToken: cancellationToken);
        if (entity.IsActive && !string.IsNullOrWhiteSpace(entity.ProxyKey))
        {
            var server = await _servers.GetAsync(entity.MediaServerId, cancellationToken: cancellationToken);
            await _client.DeleteStreamPusherProxyAsync(server, entity.ProxyKey, cancellationToken);
        }
        await _pushProxies.DeleteAsync(entity, autoSave: true, cancellationToken);
    }

    public virtual async Task<List<WebhookEventDto>> GetWebhookEventsAsync(int maxResultCount = 100, CancellationToken cancellationToken = default)
    {
        var query = await _webhooks.GetQueryableAsync();
        return await query.OrderByDescending(x => x.ReceivedAt).Take(Math.Clamp(maxResultCount, 1, 500))
            .Select(x => new WebhookEventDto { Id = x.Id, EventName = x.EventName, MediaServerId = x.MediaServerId,
                Vhost = x.Vhost, App = x.App, Stream = x.Stream, Payload = x.Payload, ReceivedAt = x.ReceivedAt })
            .ToListAsync(cancellationToken);
    }

    public virtual async Task<WebhookReceivedEto> SaveWebhookAsync(string eventName, Guid? serverId, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var entity = new WebhookEvent(GuidGenerator.Create(), eventName, serverId,
            Property(payload, "vhost"), Property(payload, "app"), Property(payload, "stream"), payload.GetRawText());
        await _webhooks.InsertAsync(entity, autoSave: true, cancellationToken);
        return new(eventName, serverId, entity.Payload);
    }

    public virtual async Task HandleWebhookAutomationAsync(WebhookReceivedEto eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.EventName is not ("on_stream_not_found" or "on_stream_none_reader")) return;
        using var payload = JsonDocument.Parse(eventData.Payload);
        var root = payload.RootElement;
        var vhost = Property(root, "vhost") ?? "__defaultVhost__";
        var app = Property(root, "app") ?? string.Empty;
        var stream = Property(root, "stream") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(stream)) return;

        var query = await _proxies.GetQueryableAsync();
        var proxy = await query.FirstOrDefaultAsync(x => x.OnDemand && x.Vhost == vhost && x.App == app && x.Stream == stream, cancellationToken);
        if (proxy is null) return;

        var server = await _servers.GetAsync(proxy.MediaServerId, cancellationToken: cancellationToken);
        if (eventData.EventName == "on_stream_not_found" && !proxy.IsActive)
        {
            // 按需代理保持 OnDemand=true，只改变当前运行状态，便于无人观看时再次回收。
            var preset = proxy.ProtocolPresetId.HasValue
                ? await _presets.FindAsync(proxy.ProtocolPresetId.Value, cancellationToken: cancellationToken)
                : null;
            var key = await _client.AddStreamProxyAsync(server, ToCreateRequest(proxy), preset, cancellationToken);
            proxy.MarkStarted(key);
        }
        else if (eventData.EventName == "on_stream_none_reader" && proxy.IsActive)
        {
            if (!string.IsNullOrWhiteSpace(proxy.ProxyKey))
                await _client.DeleteStreamProxyAsync(server, proxy.ProxyKey, cancellationToken);
            proxy.MarkStopped();
        }
        await _proxies.UpdateAsync(proxy, autoSave: true, cancellationToken);
    }

    private async Task<MediaServer> GetServerAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id.HasValue && id.Value != Guid.Empty) return await _servers.GetAsync(id.Value, cancellationToken: cancellationToken);
        var server = (await _servers.GetListAsync(x => x.IsEnabled, cancellationToken: cancellationToken)).FirstOrDefault();
        return server ?? throw new UserFriendlyException("No enabled ZLMediaKit server is configured.");
    }

    private static void ValidateServer(SaveMediaServerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new UserFriendlyException("Server name is required.");
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new UserFriendlyException("Base URL must be an absolute HTTP or HTTPS URL.");
    }
    private static int Int(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static long Long(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static string? Property(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static PullProxyDto MapProxy(PullProxy x) => new()
    {
        Id = x.Id, MediaServerId = x.MediaServerId, Vhost = x.Vhost, App = x.App, Stream = x.Stream,
        SourceUrl = x.SourceUrl, SourceUrls = DeserializeUrls(x.SourceUrlsJson), Remark = x.Remark,
        ProxyKey = x.ProxyKey, EnableHls = x.EnableHls, EnableMp4 = x.EnableMp4,
        IsActive = x.IsActive, OnDemand = x.OnDemand, ProtocolPresetId = x.ProtocolPresetId,
        RetryCount = x.RetryCount, TimeoutSeconds = x.TimeoutSeconds, RtpType = x.RtpType
    };

    private static CreatePullProxyRequest ToCreateRequest(PullProxy x) => new()
    {
        MediaServerId = x.MediaServerId, Vhost = x.Vhost, App = x.App, Stream = x.Stream,
        SourceUrls = DeserializeUrls(x.SourceUrlsJson), Remark = x.Remark, ProtocolPresetId = x.ProtocolPresetId,
        OnDemand = false, RetryCount = x.RetryCount, TimeoutSeconds = x.TimeoutSeconds,
        RtpType = x.RtpType, EnableHls = x.EnableHls, EnableMp4 = x.EnableMp4
    };

    private static List<string> DeserializeUrls(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static ProtocolPresetDto MapPreset(ProtocolPreset x) => new()
    {
        Id = x.Id, Name = x.Name, EnableAudio = x.EnableAudio, AddMuteAudio = x.AddMuteAudio,
        EnableHls = x.EnableHls, EnableMp4 = x.EnableMp4, EnableRtsp = x.EnableRtsp,
        EnableRtmp = x.EnableRtmp, EnableTs = x.EnableTs, EnableFmp4 = x.EnableFmp4,
        AutoClose = x.AutoClose, Mp4MaxSeconds = x.Mp4MaxSeconds,
        Mp4SavePath = x.Mp4SavePath, HlsSavePath = x.HlsSavePath
    };

    private static PushProxyDto MapPushProxy(PushProxy x) => new()
    {
        Id = x.Id, MediaServerId = x.MediaServerId, Schema = x.Schema, Vhost = x.Vhost,
        App = x.App, Stream = x.Stream, DestinationUrl = x.DestinationUrl, Remark = x.Remark,
        ProxyKey = x.ProxyKey, RetryCount = x.RetryCount, TimeoutSeconds = x.TimeoutSeconds,
        RtpType = x.RtpType, IsActive = x.IsActive
    };

    private static CreatePushProxyRequest ToPushRequest(PushProxy x) => new()
    {
        MediaServerId = x.MediaServerId, Schema = x.Schema, Vhost = x.Vhost, App = x.App,
        Stream = x.Stream, DestinationUrl = x.DestinationUrl, Remark = x.Remark,
        RetryCount = x.RetryCount, TimeoutSeconds = x.TimeoutSeconds, RtpType = x.RtpType
    };
}
