using System.Text.Json;
using Apiext.MediaKit.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Volo.Abp.EventBus.Local;

namespace Apiext.MediaKit.Controllers;

[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize]
[Route("api/mediakit")]
public sealed class MediaKitController : ControllerBase
{
    private readonly MediaManagementAppService _service;
    private readonly ILocalEventBus _eventBus;
    private readonly MediaKitOptions _options;
    private readonly StreamAuthService _streamAuth;
    private readonly IMediaKitPluginRegistry _plugins;
    private readonly HostStatsService _hostStats;
    private readonly IHostEnvironment _environment;
    private readonly WebhookProtection _webhookProtection;
    private readonly ILogger<MediaKitController> _logger;

    public MediaKitController(
        MediaManagementAppService service,
        ILocalEventBus eventBus,
        IOptions<MediaKitOptions> options,
        StreamAuthService streamAuth,
        IMediaKitPluginRegistry plugins,
        HostStatsService hostStats,
        IHostEnvironment environment,
        WebhookProtection webhookProtection,
        ILogger<MediaKitController> logger)
    {
        _service = service;
        _eventBus = eventBus;
        _options = options.Value;
        _streamAuth = streamAuth;
        _plugins = plugins;
        _hostStats = hostStats;
        _environment = environment;
        _webhookProtection = webhookProtection;
        _logger = logger;
    }

    [HttpGet("servers")]
    public Task<List<MediaServerDto>> GetServers(CancellationToken cancellationToken) => _service.GetServersAsync(cancellationToken);
    [HttpPost("servers")]
    public Task<MediaServerDto> CreateServer(SaveMediaServerRequest request, CancellationToken cancellationToken) => _service.CreateServerAsync(request, cancellationToken);
    [HttpPut("servers/{id:guid}")]
    public Task<MediaServerDto> UpdateServer(Guid id, SaveMediaServerRequest request, CancellationToken cancellationToken) => _service.UpdateServerAsync(id, request, cancellationToken);
    [HttpDelete("servers/{id:guid}")]
    public Task DeleteServer(Guid id, CancellationToken cancellationToken) => _service.DeleteServerAsync(id, cancellationToken);

    [HttpGet("dashboard")]
    public Task<DashboardDto> GetDashboard(Guid? serverId, CancellationToken cancellationToken) => _service.GetDashboardAsync(serverId, cancellationToken);
    [HttpGet("host-stats")]
    public HostStatsDto GetHostStats() => _hostStats.GetStats();

    [HttpGet("streams")]
    public Task<List<MediaStreamDto>> GetStreams(Guid? serverId, CancellationToken cancellationToken) => _service.GetMediaAsync(serverId, cancellationToken);
    [HttpDelete("streams")]
    public Task CloseStream(Guid? serverId, string schema, string vhost, string app, string stream, CancellationToken cancellationToken)
        => _service.CloseMediaAsync(serverId, schema, vhost, app, stream, cancellationToken);
    [HttpGet("streams/play-info")]
    public Task<StreamPlayInfoDto> GetPlayInfo(Guid? serverId, string vhost, string app, string stream, CancellationToken cancellationToken)
        => _service.GetPlayInfoAsync(serverId, vhost, app, stream, cancellationToken);
    [HttpGet("streams/viewers")]
    public Task<List<StreamViewerDto>> GetViewers(Guid? serverId, string schema, string vhost, string app, string stream, CancellationToken cancellationToken)
        => _service.GetViewersAsync(serverId, schema, vhost, app, stream, cancellationToken);

    [HttpGet("pull-proxies")]
    public Task<List<PullProxyDto>> GetPullProxies(Guid? serverId, CancellationToken cancellationToken) => _service.GetPullProxiesAsync(serverId, cancellationToken);
    [HttpPost("pull-proxies")]
    public Task<PullProxyDto> CreatePullProxy(CreatePullProxyRequest request, CancellationToken cancellationToken) => _service.CreatePullProxyAsync(request, cancellationToken);
    [HttpPut("pull-proxies/{id:guid}")]
    public Task<PullProxyDto> UpdatePullProxy(Guid id, CreatePullProxyRequest request, CancellationToken cancellationToken)
        => _service.UpdatePullProxyAsync(id, request, cancellationToken);
    [HttpDelete("pull-proxies/{id:guid}")]
    public Task StopPullProxy(Guid id, CancellationToken cancellationToken) => _service.StopPullProxyAsync(id, cancellationToken);
    [HttpDelete("pull-proxies/{id:guid}/configuration")]
    public Task DeletePullProxy(Guid id, CancellationToken cancellationToken) => _service.DeletePullProxyAsync(id, cancellationToken);
    [HttpPost("pull-proxies/{id:guid}/toggle-mode")]
    public Task TogglePullProxyMode(Guid id, CancellationToken cancellationToken) => _service.TogglePullProxyModeAsync(id, cancellationToken);

    [HttpGet("protocol-presets")]
    public Task<List<ProtocolPresetDto>> GetProtocolPresets(CancellationToken cancellationToken) => _service.GetProtocolPresetsAsync(cancellationToken);
    [HttpPost("protocol-presets")]
    public Task<ProtocolPresetDto> CreateProtocolPreset(SaveProtocolPresetRequest request, CancellationToken cancellationToken)
        => _service.SaveProtocolPresetAsync(null, request, cancellationToken);
    [HttpPut("protocol-presets/{id:guid}")]
    public Task<ProtocolPresetDto> UpdateProtocolPreset(Guid id, SaveProtocolPresetRequest request, CancellationToken cancellationToken)
        => _service.SaveProtocolPresetAsync(id, request, cancellationToken);
    [HttpDelete("protocol-presets/{id:guid}")]
    public Task DeleteProtocolPreset(Guid id, CancellationToken cancellationToken) => _service.DeleteProtocolPresetAsync(id, cancellationToken);

    [HttpGet("sessions")]
    public Task<List<NetworkSessionDto>> GetSessions(Guid? serverId, CancellationToken cancellationToken)
        => _service.GetSessionsAsync(serverId, cancellationToken);
    [HttpDelete("sessions/{sessionId}")]
    public Task KickSession(string sessionId, Guid? serverId, CancellationToken cancellationToken)
        => _service.KickSessionAsync(serverId, sessionId, cancellationToken);

    [HttpPost("recordings/start")]
    public Task StartRecord(RecordOperationRequest request, CancellationToken cancellationToken) => _service.StartRecordAsync(request, cancellationToken);
    [HttpPost("recordings/stop")]
    public Task StopRecord(RecordOperationRequest request, CancellationToken cancellationToken) => _service.StopRecordAsync(request, cancellationToken);
    [HttpGet("recordings")]
    public Task<RecordSearchResultDto> GetRecordFiles(Guid? serverId, string vhost, string app, string stream, string period, CancellationToken cancellationToken)
        => _service.GetRecordFilesAsync(serverId, vhost, app, stream, period, cancellationToken);

    [HttpGet("configuration")]
    public Task<List<ConfigurationEntryDto>> GetConfiguration(Guid? serverId, CancellationToken cancellationToken) => _service.GetConfigurationAsync(serverId, cancellationToken);
    [HttpPut("configuration")]
    public Task SaveConfiguration(SaveConfigurationRequest request, CancellationToken cancellationToken) => _service.SaveConfigurationAsync(request, cancellationToken);

    [HttpGet("whip-info")]
    public Task<WhipInfoDto> GetWhipInfo(Guid? serverId, string app, string stream, CancellationToken cancellationToken)
        => _service.GetWhipInfoAsync(serverId, app, stream, cancellationToken);

    [HttpGet("push-proxies")]
    public Task<List<PushProxyDto>> GetPushProxies(Guid? serverId, CancellationToken cancellationToken)
        => _service.GetPushProxiesAsync(serverId, cancellationToken);
    [HttpPost("push-proxies")]
    public Task<PushProxyDto> CreatePushProxy(CreatePushProxyRequest request, CancellationToken cancellationToken)
        => _service.CreatePushProxyAsync(request, cancellationToken);
    [HttpPost("push-proxies/{id:guid}/start")]
    public Task StartPushProxy(Guid id, CancellationToken cancellationToken)
        => _service.StartPushProxyAsync(id, cancellationToken);
    [HttpPost("push-proxies/{id:guid}/stop")]
    public Task StopPushProxy(Guid id, CancellationToken cancellationToken)
        => _service.StopPushProxyAsync(id, cancellationToken);
    [HttpDelete("push-proxies/{id:guid}")]
    public Task DeletePushProxy(Guid id, CancellationToken cancellationToken)
        => _service.DeletePushProxyAsync(id, cancellationToken);

    [HttpGet("webhooks")]
    public Task<List<WebhookEventDto>> GetWebhookEvents(int maxResultCount = 100, CancellationToken cancellationToken = default)
        => _service.GetWebhookEventsAsync(maxResultCount, cancellationToken);

    [HttpGet("plugins")]
    public IEnumerable<PluginInfoDto> GetPlugins()
        => _plugins.Plugins.Select(p => new PluginInfoDto(p.Name, p.Description, p.Enabled));

    [HttpGet("security/status")]
    public SecurityStatusDto GetSecurityStatus()
    {
        var adminPassword = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Authentication:Password"] ?? string.Empty;
        var weakList = _options.Security.WeakPasswords ?? [];
        var weak = weakList.Any(w => string.Equals(w ?? string.Empty, adminPassword, StringComparison.Ordinal));
        return new SecurityStatusDto(
            WeakAdminPassword: weak,
            WebhookSecretConfigured: !string.IsNullOrWhiteSpace(_options.WebhookSecret)
                                     && !string.Equals(_options.WebhookSecret, "change-webhook-secret", StringComparison.OrdinalIgnoreCase),
            StreamAuthEnabled: _streamAuth.Enabled,
            StreamAuthKeyIsDefault: string.IsNullOrWhiteSpace(_options.StreamAuth.SigningKey)
                                    || string.Equals(_options.StreamAuth.SigningKey, "change-stream-auth-key", StringComparison.OrdinalIgnoreCase),
            EnvironmentName: _environment.EnvironmentName,
            WebhookUrlHint: $"{Request.Scheme}://{Request.Host}/api/mediakit/webhooks/{{eventName}}?serverId={{instanceId}}&secret=***");
    }

    [HttpPost("security/stream-token")]
    public ActionResult<StreamTokenDto> IssueStreamToken([FromBody] IssueStreamTokenRequest request)
    {
        if (!_streamAuth.Enabled)
            return BadRequest(new { message = "Stream auth is disabled." });
        var result = _streamAuth.IssueToken(request.App, request.Stream, request.Purpose, request.TtlMinutes);
        var query = $"token={Uri.EscapeDataString(result.Token)}";
        return Ok(new StreamTokenDto(
            result.Token,
            result.ExpiresAtUnix,
            result.Purpose,
            result.App,
            result.Stream,
            result.TtlMinutes,
            $"?{query}",
            $"&{query}"));
    }

    /// <summary>
    /// ZLM hook entry. Requires MediaKit:WebhookSecret via query/header unless secret is empty (dev only).
    /// on_play / on_publish are gated by StreamAuthService when enabled.
    /// </summary>
    [HttpPost("webhooks/{eventName}")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public async Task<IActionResult> ReceiveWebhook(string eventName, [FromQuery] Guid? serverId, [FromBody] JsonElement payload, CancellationToken cancellationToken)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var allowed = _options.Webhook.AllowedIps ?? [];
        if (!_webhookProtection.IsIpAllowed(remoteIp, allowed))
        {
            _logger.LogWarning("Webhook rejected by IP allow-list: {Ip} event={Event}", remoteIp, eventName);
            return StatusCode(StatusCodes.Status403Forbidden, new { code = -1, msg = "webhook ip not allowed" });
        }

        if (!_webhookProtection.TryAcquire(remoteIp, _options.Webhook.RateLimitPerMinute))
        {
            _logger.LogWarning("Webhook rate limited: {Ip} event={Event}", remoteIp, eventName);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { code = -1, msg = "webhook rate limited" });
        }

        if (!ValidateWebhookSecret())
            return Unauthorized(new { code = -1, msg = "invalid webhook secret" });

        // Auth hooks must return ZLM-compatible code before side effects when denied.
        if (eventName is "on_play" or "on_publish")
        {
            var decision = _streamAuth.ValidateWebhook(eventName, payload);
            if (!decision.Allowed)
            {
                // Still record denied attempts for audit.
                await _service.SaveWebhookAsync(eventName + ":denied", serverId, payload, cancellationToken);
                return Ok(new { code = -1, msg = decision.Reason });
            }
        }

        var eventData = await _service.SaveWebhookAsync(eventName, serverId, payload, cancellationToken);
        await _service.HandleWebhookAutomationAsync(eventData, cancellationToken);
        await _eventBus.PublishAsync(eventData, onUnitOfWorkComplete: false);
        return Ok(new { code = 0, msg = "success" });
    }

    private bool ValidateWebhookSecret()
    {
        var expected = _options.WebhookSecret ?? string.Empty;
        // Empty secret = open (only acceptable for local experiments).
        if (string.IsNullOrWhiteSpace(expected)) return true;

        string? provided = Request.Query["secret"].FirstOrDefault()
                           ?? Request.Headers["X-MediaKit-Webhook-Secret"].FirstOrDefault()
                           ?? Request.Headers["X-Webhook-Secret"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided)) return false;

        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
