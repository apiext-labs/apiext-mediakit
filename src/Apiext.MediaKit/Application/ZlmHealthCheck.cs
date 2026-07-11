using Apiext.MediaKit.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Volo.Abp.Domain.Repositories;

namespace Apiext.MediaKit;

public sealed class ZlmHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaKitOptions _options;

    public ZlmHealthCheck(IServiceScopeFactory scopeFactory, IOptions<MediaKitOptions> options)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Health.ProbeDefaultZlm)
            return HealthCheckResult.Healthy("ZLM probe disabled");

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var servers = scope.ServiceProvider.GetRequiredService<IRepository<MediaServer, Guid>>();
            var list = await servers.GetListAsync(x => x.IsEnabled, cancellationToken: cancellationToken);
            var server = list.OrderBy(x => x.Name).FirstOrDefault();
            if (server is null)
                return HealthCheckResult.Degraded("No enabled MediaServer configured");

            var client = scope.ServiceProvider.GetRequiredService<ZLMediaKitClient>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Health.ProbeTimeoutSeconds, 1, 30)));
            _ = await client.GetConfigurationAsync(server, cts.Token);
            return HealthCheckResult.Healthy($"ZLM ok: {server.Name} ({server.BaseUrl})");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("ZLM probe failed", ex);
        }
    }
}
