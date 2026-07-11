using AntDesign.ProLayout;
using Apiext.MediaKit.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Apiext.MediaKit
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.Services.AddScoped(
                sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

            AddClientServices(builder.Services);
            builder.Services.Configure<ProSettings>(builder.Configuration.GetSection("ProSettings"));
            await builder.Build().RunAsync();
        }

        public static void AddClientServices(IServiceCollection services)
        {
            services.AddAntDesign();
            services.AddAuthorizationCore();
            // Host overrides this with HttpContextAmbientUserAccessor after AddClientServices.
            services.AddScoped<IAmbientUserAccessor, NullAmbientUserAccessor>();
            services.AddScoped<ApiAuthenticationStateProvider>();
            services.AddScoped<AuthenticationStateProvider>(provider =>
                provider.GetRequiredService<ApiAuthenticationStateProvider>());
            services.AddScoped<MediaKitApiClient>();
            services.AddScoped<MediaServerContext>();
        }
    }
}
