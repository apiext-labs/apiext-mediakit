using Apiext.MediaKit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Autofac;
using Volo.Abp.BackgroundWorkers;
using Volo.Abp.Application;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Sqlite;
using Volo.Abp.Modularity;

namespace Apiext.MediaKit;

[DependsOn(
    typeof(AbpDddApplicationModule),
    typeof(AbpAutofacModule),
    typeof(AbpAuthorizationModule),
    typeof(AbpBackgroundWorkersModule),
    typeof(AbpEntityFrameworkCoreSqliteModule))]
public sealed class ApiextMediaKitModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Default is required.");

        context.Services.Configure<MediaKitOptions>(configuration.GetSection(MediaKitOptions.SectionName));

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure<MediaKitDbContext>(configuration =>
                configuration.DbContextOptions.UseSqlite(connectionString));
        });
        context.Services.AddAbpDbContext<MediaKitDbContext>(options =>
        {
            options.AddDefaultRepositories(includeAllEntities: true);
        });

        context.Services.AddHttpClient("ZLMediaKit", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        context.Services.AddScoped<ZLMediaKitClient>();
        context.Services.AddScoped<MediaManagementAppService>();
        context.Services.AddSingleton<IMediaKitPluginRegistry, MediaKitPluginRegistry>();
        context.Services.AddSingleton<StreamAuthService>();
        context.Services.AddSingleton<HostStatsService>();
        context.Services.AddSingleton<WebhookProtection>();
        context.Services.AddSingleton<IMediaKitPlugin, StreamAuthPlugin>();
        context.Services.AddSingleton<IMediaKitPlugin, OnDemandPullProxyPlugin>();

        context.Services.AddHealthChecks()
            .AddDbContextCheck<MediaKitDbContext>("sqlite")
            .AddCheck<ZlmHealthCheck>("zlm");
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var registry = context.ServiceProvider.GetRequiredService<IMediaKitPluginRegistry>();
        foreach (var plugin in context.ServiceProvider.GetServices<IMediaKitPlugin>())
            registry.Register(plugin);
    }
}
