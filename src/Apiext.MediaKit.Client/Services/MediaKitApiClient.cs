using System.Net.Http.Json;
using Apiext.MediaKit.Models;

namespace Apiext.MediaKit.Services;

public sealed class MediaKitApiClient
{
    private readonly HttpClient _http;
    public MediaKitApiClient(HttpClient http) => _http = http;

    public async Task<List<MediaServerDto>> GetServersAsync() => await _http.GetFromJsonAsync<List<MediaServerDto>>("api/mediakit/servers") ?? [];
    public async Task<MediaServerDto?> CreateServerAsync(SaveMediaServerRequest request)
        => await PostAsync<SaveMediaServerRequest, MediaServerDto>("api/mediakit/servers", request);
    public async Task DeleteServerAsync(Guid id) => await EnsureAsync(await _http.DeleteAsync($"api/mediakit/servers/{id}"));
    public async Task<DashboardDto?> GetDashboardAsync(Guid? serverId) => await _http.GetFromJsonAsync<DashboardDto>(WithServer("api/mediakit/dashboard", serverId));
    public async Task<List<MediaStreamDto>> GetStreamsAsync(Guid? serverId) => await _http.GetFromJsonAsync<List<MediaStreamDto>>(WithServer("api/mediakit/streams", serverId)) ?? [];
    public async Task CloseStreamAsync(Guid? serverId, MediaStreamDto stream)
    {
        var url = AddQuery("api/mediakit/streams", new Dictionary<string, string?>
        {
            ["serverId"] = serverId?.ToString(), ["schema"] = stream.Schema, ["vhost"] = stream.Vhost,
            ["app"] = stream.App, ["stream"] = stream.Stream
        });
        await EnsureAsync(await _http.DeleteAsync(url));
    }

    public async Task<StreamPlayInfoDto?> GetPlayInfoAsync(Guid? serverId, string vhost, string app, string stream)
    {
        var url = AddQuery("api/mediakit/streams/play-info", new Dictionary<string, string?>
        {
            ["serverId"] = serverId?.ToString(), ["vhost"] = vhost, ["app"] = app, ["stream"] = stream
        });
        return await _http.GetFromJsonAsync<StreamPlayInfoDto>(url);
    }

    public async Task<List<StreamViewerDto>> GetViewersAsync(Guid? serverId, string schema, string vhost, string app, string stream)
    {
        var url = AddQuery("api/mediakit/streams/viewers", new Dictionary<string, string?>
        {
            ["serverId"] = serverId?.ToString(), ["schema"] = schema, ["vhost"] = vhost, ["app"] = app, ["stream"] = stream
        });
        return await _http.GetFromJsonAsync<List<StreamViewerDto>>(url) ?? [];
    }

    public async Task<List<PullProxyDto>> GetPullProxiesAsync(Guid? serverId) => await _http.GetFromJsonAsync<List<PullProxyDto>>(WithServer("api/mediakit/pull-proxies", serverId)) ?? [];
    public async Task<PullProxyDto?> CreatePullProxyAsync(CreatePullProxyRequest request)
        => await PostAsync<CreatePullProxyRequest, PullProxyDto>("api/mediakit/pull-proxies", request);
    public async Task<PullProxyDto?> UpdatePullProxyAsync(Guid id, CreatePullProxyRequest request)
    {
        var response = await _http.PutAsJsonAsync($"api/mediakit/pull-proxies/{id}", request);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<PullProxyDto>();
    }
    public async Task StopPullProxyAsync(Guid id) => await EnsureAsync(await _http.DeleteAsync($"api/mediakit/pull-proxies/{id}"));
    public async Task DeletePullProxyAsync(Guid id) => await EnsureAsync(await _http.DeleteAsync($"api/mediakit/pull-proxies/{id}/configuration"));
    public async Task TogglePullProxyModeAsync(Guid id) => await PostAsync($"api/mediakit/pull-proxies/{id}/toggle-mode", new { });
    public async Task<List<ProtocolPresetDto>> GetProtocolPresetsAsync() => await _http.GetFromJsonAsync<List<ProtocolPresetDto>>("api/mediakit/protocol-presets") ?? [];
    public async Task<ProtocolPresetDto?> SaveProtocolPresetAsync(SaveProtocolPresetRequest request)
        => await PostAsync<SaveProtocolPresetRequest, ProtocolPresetDto>("api/mediakit/protocol-presets", request);
    public async Task DeleteProtocolPresetAsync(Guid id) => await EnsureAsync(await _http.DeleteAsync($"api/mediakit/protocol-presets/{id}"));
    public async Task<List<NetworkSessionDto>> GetSessionsAsync(Guid? serverId) => await _http.GetFromJsonAsync<List<NetworkSessionDto>>(WithServer("api/mediakit/sessions", serverId)) ?? [];
    public async Task KickSessionAsync(Guid? serverId, string sessionId)
        => await EnsureAsync(await _http.DeleteAsync(WithServer($"api/mediakit/sessions/{Uri.EscapeDataString(sessionId)}", serverId)));
    public async Task StartRecordAsync(RecordOperationRequest request) => await PostAsync("api/mediakit/recordings/start", request);
    public async Task StopRecordAsync(RecordOperationRequest request) => await PostAsync("api/mediakit/recordings/stop", request);
    public async Task<RecordSearchResultDto?> GetRecordFilesAsync(Guid serverId, string vhost, string app, string stream, string period)
    {
        var url = AddQuery("api/mediakit/recordings", new Dictionary<string, string?>
        {
            ["serverId"] = serverId.ToString(), ["vhost"] = vhost, ["app"] = app,
            ["stream"] = stream, ["period"] = period
        });
        return await _http.GetFromJsonAsync<RecordSearchResultDto>(url);
    }
    public async Task<List<ConfigurationEntryDto>> GetConfigurationAsync(Guid? serverId) => await _http.GetFromJsonAsync<List<ConfigurationEntryDto>>(WithServer("api/mediakit/configuration", serverId)) ?? [];
    public async Task SaveConfigurationAsync(SaveConfigurationRequest request)
        => await EnsureAsync(await _http.PutAsJsonAsync("api/mediakit/configuration", request));

    public async Task<WhipInfoDto?> GetWhipInfoAsync(Guid? serverId, string app, string stream)
    {
        var url = AddQuery("api/mediakit/whip-info", new Dictionary<string, string?>
        {
            ["serverId"] = serverId?.ToString(), ["app"] = app, ["stream"] = stream
        });
        return await _http.GetFromJsonAsync<WhipInfoDto>(url);
    }

    public async Task<List<PushProxyDto>> GetPushProxiesAsync(Guid? serverId)
        => await _http.GetFromJsonAsync<List<PushProxyDto>>(WithServer("api/mediakit/push-proxies", serverId)) ?? [];
    public async Task<PushProxyDto?> CreatePushProxyAsync(CreatePushProxyRequest request)
        => await PostAsync<CreatePushProxyRequest, PushProxyDto>("api/mediakit/push-proxies", request);
    public async Task StartPushProxyAsync(Guid id)
        => await PostAsync($"api/mediakit/push-proxies/{id}/start", new { });
    public async Task StopPushProxyAsync(Guid id)
        => await PostAsync($"api/mediakit/push-proxies/{id}/stop", new { });
    public async Task DeletePushProxyAsync(Guid id)
        => await EnsureAsync(await _http.DeleteAsync($"api/mediakit/push-proxies/{id}"));

    public async Task<List<WebhookEventDto>> GetWebhooksAsync() => await _http.GetFromJsonAsync<List<WebhookEventDto>>("api/mediakit/webhooks") ?? [];
    public async Task<List<PluginInfoDto>> GetPluginsAsync() => await _http.GetFromJsonAsync<List<PluginInfoDto>>("api/mediakit/plugins") ?? [];
    public async Task<SecurityStatusDto?> GetSecurityStatusAsync() => await _http.GetFromJsonAsync<SecurityStatusDto>("api/mediakit/security/status");
    public async Task<StreamTokenDto?> IssueStreamTokenAsync(IssueStreamTokenRequest request)
        => await PostAsync<IssueStreamTokenRequest, StreamTokenDto>("api/mediakit/security/stream-token", request);
    public async Task<HostStatsDto?> GetHostStatsAsync() => await _http.GetFromJsonAsync<HostStatsDto>("api/mediakit/host-stats");

    private static string WithServer(string url, Guid? serverId) => serverId.HasValue
        ? $"{url}?serverId={Uri.EscapeDataString(serverId.Value.ToString())}" : url;
    private static string AddQuery(string url, IReadOnlyDictionary<string, string?> values)
        => $"{url}?{string.Join("&", values.Where(x => x.Value is not null).Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"))}";
    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string url, TRequest request)
    {
        var response = await _http.PostAsJsonAsync(url, request);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<TResponse>();
    }
    private async Task PostAsync<TRequest>(string url, TRequest request) => await EnsureAsync(await _http.PostAsJsonAsync(url, request));
    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase : message);
    }
}
