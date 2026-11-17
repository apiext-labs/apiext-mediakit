using System.Text;
using AntDesign.ProLayout;
using Apiext.MediaKit.Server.Apis;
using Apiext.MediaKit.Server.Auth;
using Apiext.MediaKit.Server.Gb28181;
using Apiext.MediaKit.Server.Persistence;
using Apiext.MediaKit.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WebApiClientCore;

namespace Apiext.MediaKit.Server.Extensions;

/// <summary>服务注册收敛：Blazor / AntDesign / EF Core / ZLM / 域服务 / 后台服务 / 认证。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>一次性注册 Apiext MediaKit 全部服务。</summary>
    public static IServiceCollection AddApiextMediaKit(this IServiceCollection services, IConfiguration configuration)
    {
        // Blazor (InteractiveServer)
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddHttpContextAccessor();

        // MVC Controller：所有 HTTP API 入口统一放在 Controllers/（不再用 minimal API 的 *Endpoints 映射）
        services.AddControllers();

        // Ant Design + ProLayout
        services.AddAntDesign();
        services.Configure<ProSettings>(configuration.GetSection("ProSettings"));

        // EF Core + SQLite
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString("Default")));

        // WebApiClientCore -> ZLMediaKit
        services.AddHttpApi<IZlMediaKitApi>(options =>
        {
            options.HttpHost = new Uri(configuration["ZlMediaKit:BaseUrl"]!);
        });

        // ONVIF SOAP 命名客户端：连接池复用，避免每次操作 new HttpClient 累积泄漏
        services.AddHttpClient("onvif")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));

        services.AddScoped<ZlmService>();
        services.AddScoped<PullProxyService>();
        services.AddScoped<OnvifService>();
        services.AddScoped<DeviceListService>();
        services.AddScoped<RecordingBackupService>();
        services.AddScoped<TokenService>();
        services.AddScoped<DeviceGroupService>();

        // ZLM WebHook：配置 + 在线流缓存（单例）+ 事件处理
        services.Configure<ZlmHookOptions>(configuration.GetSection("ZlmHook"));
        services.AddSingleton<ZlmOnlineCache>();
        services.AddScoped<ZlmHookService>();

        // 播放令牌：短时效签名 URL，ZLM on_play / on_http_access 校验
        services.Configure<PlayTokenOptions>(configuration.GetSection("PlayToken"));
        services.AddScoped<PlayTokenService>();

        // GB28181：SIP 信令引擎（单例后台服务）+ 落库/鉴权 + 业务编排
        services.AddSingleton<GbDeviceStore>();
        services.AddSingleton<GbAuthenticator>();
        services.AddSingleton<GbSipEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<GbSipEngine>());
        services.AddScoped<Gb28181Service>();

        // GB28181 平台级联：下级平台向上级平台注册（单例后台服务）
        services.AddSingleton<GbCascadeEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<GbCascadeEngine>());

        // 无人观看自动断流（单例后台服务）
        services.AddSingleton<GbPlayWatchdog>();
        services.AddHostedService(sp => sp.GetRequiredService<GbPlayWatchdog>());

        // 按需拉流：无人观看自动断流
        services.AddHostedService<OnDemandPullService>();

        // 启动恢复：把非按需（以及按需但录像）的拉流代理重新拉起（ZLM 重启会丢内存态）
        services.AddHostedService<PullProxyRestoreService>();

        // 录像备份：定时同步到 SMB/NFS/S3 挂载目录
        services.AddHostedService<RecordingBackupHostedService>();

        // 录像保留期清理：按 Recording:RetentionDays 删除过期文件（0 = 不清理）
        services.AddHostedService<RecordingRetentionService>();

        // 录像计划：按 Recording:Schedule 时间段批量启停录像（留空 = 不干预）
        services.AddHostedService<RecordingScheduleService>();

        // 录像索引对账：hook 为主链路，本服务校正「索引有/文件没了」与「文件在/索引没有」
        services.AddHostedService<RecordingReconcileService>();

        // 认证：Cookie 用于页面授权（Blazor InteractiveServer），JWT Bearer 用于 REST API 鉴权
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
            })
            .AddJwtBearer("Bearer", options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = configuration["Jwt:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = configuration["Jwt:Audience"],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!)),
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });
        services.AddAuthorization(options =>
        {
            // 显式注册 "Bearer" 授权策略，供 REST API 端点 RequireAuthorization("Bearer") 使用
            options.AddPolicy("Bearer", policy => policy.RequireAuthenticatedUser().AddAuthenticationSchemes("Bearer"));
        });
        services.AddCascadingAuthenticationState();

        services.AddScoped<JwtTokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<AccountService>();
        services.AddScoped<AppSettingsService>();

        // Swagger / OpenAPI 文档（/api/docs，访问令牌鉴权）
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
            {
                Title = "Apiext MediaKit API",
                Version = "v1",
                Description = "流媒体管理平台对外 REST API。鉴权：在「高级配置 → 访问令牌」生成令牌，填入 Authorization: Bearer <token>。",
            });
            options.AddSecurityDefinition("ApiToken", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                Scheme = "bearer",
                In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                Description = "访问令牌（高级配置 → 访问令牌 生成）",
            });
            options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
            {
                {
                    new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                    {
                        Reference = new Microsoft.OpenApi.Models.OpenApiReference
                        {
                            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                            Id = "ApiToken",
                        },
                    },
                    Array.Empty<string>()
                },
            });
        });

        // HttpClient（BaseAddress 指向自身，供登录等内部 API 调用）
        services.AddScoped(sp =>
        {
            var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
            if (httpContext != null)
            {
                return new HttpClient
                {
                    BaseAddress = new Uri(httpContext.Request.Scheme + "://" + httpContext.Request.Host)
                };
            }
            return new HttpClient();
        });

        return services;
    }
}
