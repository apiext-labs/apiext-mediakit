using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;

namespace Apiext.MediaKit;

public sealed record WebhookReceivedEto(string EventName, Guid? MediaServerId, string Payload);

public interface IMediaKitPlugin
{
    string Name { get; }
    string Description => Name;
    bool Enabled => true;
    Task OnWebhookAsync(WebhookReceivedEto eventData, CancellationToken cancellationToken = default);
}

public interface IMediaKitPluginRegistry
{
    IReadOnlyCollection<IMediaKitPlugin> Plugins { get; }
    void Register(IMediaKitPlugin plugin);
}

public sealed class MediaKitPluginRegistry : IMediaKitPluginRegistry
{
    private readonly List<IMediaKitPlugin> _plugins = [];
    private readonly object _sync = new();

    public IReadOnlyCollection<IMediaKitPlugin> Plugins
    {
        get { lock (_sync) return _plugins.ToArray(); }
    }

    public void Register(IMediaKitPlugin plugin)
    {
        lock (_sync)
        {
            if (_plugins.All(x => x.Name != plugin.Name))
                _plugins.Add(plugin);
        }
    }
}

public sealed class WebhookPluginDispatcher : ILocalEventHandler<WebhookReceivedEto>, ITransientDependency
{
    private readonly IMediaKitPluginRegistry _registry;
    public WebhookPluginDispatcher(IMediaKitPluginRegistry registry) => _registry = registry;

    public async Task HandleEventAsync(WebhookReceivedEto eventData)
    {
        // Persist first (controller), then dispatch; plugins must not own HTTP lifecycle.
        foreach (var plugin in _registry.Plugins.Where(x => x.Enabled))
            await plugin.OnWebhookAsync(eventData);
    }
}

public sealed class OnDemandPullProxyPlugin : IMediaKitPlugin
{
    public string Name => "on-demand-pull-proxy";
    public string Description => "按需拉流：on_stream_not_found / on_stream_none_reader 自动化";
    public bool Enabled => true;

    // Actual work stays in MediaManagementAppService.HandleWebhookAutomationAsync for transactional access.
    public Task OnWebhookAsync(WebhookReceivedEto eventData, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
