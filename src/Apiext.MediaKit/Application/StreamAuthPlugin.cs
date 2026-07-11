using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Apiext.MediaKit;

/// <summary>
/// Observability plugin: logs stream auth decisions already enforced at the webhook HTTP boundary.
/// </summary>
public sealed class StreamAuthPlugin : IMediaKitPlugin
{
    private readonly ILogger<StreamAuthPlugin> _logger;
    public StreamAuthPlugin(ILogger<StreamAuthPlugin> logger) => _logger = logger;

    public string Name => "stream-auth";
    public string Description => "播放/推流 Token 鉴权（on_play / on_publish）";
    public bool Enabled => true;

    public Task OnWebhookAsync(WebhookReceivedEto eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.EventName is not ("on_play" or "on_publish")) return Task.CompletedTask;
        try
        {
            using var doc = JsonDocument.Parse(eventData.Payload);
            var app = doc.RootElement.TryGetProperty("app", out var a) ? a.ToString() : "";
            var stream = doc.RootElement.TryGetProperty("stream", out var s) ? s.ToString() : "";
            _logger.LogInformation("StreamAuth plugin observed {Event} for {App}/{Stream}", eventData.EventName, app, stream);
        }
        catch
        {
            // ignore parse errors
        }
        return Task.CompletedTask;
    }
}
