using System.Security.Claims;
using Apiext.MediaKit;
using Apiext.MediaKit.Components;
using Apiext.MediaKit.Domain;
using Apiext.MediaKit.EntityFrameworkCore;
using Apiext.MediaKit.HostServices;
using Apiext.MediaKit.Services;
using AntDesign.ProLayout;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseAutofac();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
var configuredConnection = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(configuredConnection))
{
    builder.Configuration["ConnectionStrings:Default"] = $"Data Source={Path.Combine(dataDirectory, "apiext-mediakit.db")}";
}
else if (configuredConnection.StartsWith("Data Source=App_Data/", StringComparison.OrdinalIgnoreCase))
{
    var relativeFile = configuredConnection["Data Source=App_Data/".Length..];
    builder.Configuration["ConnectionStrings:Default"] = $"Data Source={Path.Combine(dataDirectory, relativeFile)}";
}
builder.Services.ReplaceConfiguration(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddHttpContextAccessor();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust reverse proxy on same host / docker network by default.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddControllers();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Apiext.MediaKit.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/user/login";
        options.AccessDeniedPath = "/user/login";
        options.Events.OnRedirectToLogin = context =>
        {
            // APIs: never HTML-redirect (would confuse HttpClient / cause odd loops).
            if (context.Request.Path.StartsWithSegments("/api")
                || context.Request.Headers.Accept.Any(v => v?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            // Already on the login page → stop. This is the classic ERR_TOO_MANY_REDIRECTS fix.
            if (context.Request.Path.StartsWithSegments("/user/login"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/user/login"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect("/user/login");
            return Task.CompletedTask;
        };
    });

// Page access is enforced by Blazor [Authorize] / AuthorizeRouteView — NOT a global fallback policy.
// A FallbackPolicy of RequireAuthenticatedUser makes /user/login itself challenge → redirect loop.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = null;
    options.DefaultPolicy = new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAntDesign();

builder.Services.AddTransient<CookieForwardingHandler>();
builder.Services.AddScoped(sp =>
{
    var accessor = sp.GetRequiredService<IHttpContextAccessor>();
    var handler = sp.GetRequiredService<CookieForwardingHandler>();
    handler.InnerHandler = new HttpClientHandler
    {
        UseCookies = false,
        // Do not follow 302 HTML login redirects when calling JSON APIs.
        AllowAutoRedirect = false
    };

    var client = new HttpClient(handler);
    var httpContext = accessor.HttpContext;
    if (httpContext is not null)
        client.BaseAddress = new Uri($"{httpContext.Request.Scheme}://{httpContext.Request.Host.Value}");
    else
        client.BaseAddress = new Uri("http://localhost:5082");
    return client;
});

Apiext.MediaKit.Program.AddClientServices(builder.Services);
builder.Services.AddScoped<IAmbientUserAccessor, HttpContextAmbientUserAccessor>();
// Prefer the real cookie principal for Blazor auth state on the host (SSR / Interactive Server).
builder.Services.AddScoped<AuthenticationStateProvider, HostCookieAuthenticationStateProvider>();

builder.Services.Configure<ProSettings>(builder.Configuration.GetSection("ProSettings"));

var abpApplication = await builder.Services.AddApplicationAsync<global::Apiext.MediaKit.ApiextMediaKitModule>();

// ABP modules may reconfigure authorization — force no global fallback after module setup.
builder.Services.PostConfigure<AuthorizationOptions>(options =>
{
    options.FallbackPolicy = null;
});

var app = builder.Build();

SecurityStartupGuard.Validate(app.Configuration, app.Environment, app.Logger);

await abpApplication.InitializeAsync(app.Services);

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MediaKitDbContext>();
    app.Logger.LogInformation("Migrating MediaKit database at {DataSource}", db.Database.GetDbConnection().DataSource);
    try
    {
        // Prefer EF migrations for new installs.
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        // Databases previously created with EnsureCreated may lack __EFMigrationsHistory.
        app.Logger.LogWarning(ex, "EF Migrate failed; falling back to EnsureCreated + schema bootstrap");
        await db.Database.EnsureCreatedAsync();
    }
    await MediaKitSchemaBootstrapper.UpgradeAsync(db);
    await db.Database.CloseConnectionAsync();
}
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MediaKitDbContext>();
    app.Logger.LogInformation("Seeding MediaKit database at {DataSource}", db.Database.GetDbConnection().DataSource);
    if (!await db.MediaServers.AnyAsync())
    {
        var defaults = builder.Configuration.GetSection("MediaKit:DefaultServer");
        db.MediaServers.Add(new MediaServer(Guid.NewGuid(), defaults["Name"] ?? "Local ZLMediaKit",
            defaults["BaseUrl"] ?? "http://127.0.0.1:8080", defaults["Secret"] ?? string.Empty));
        await db.SaveChangesAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Pure HTTP only (TLS terminates at reverse proxy when used).
app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapControllers();
app.MapHealthChecks("/health");
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Apiext.MediaKit.Client._Imports).Assembly);

app.Run();
