using Apiext.MediaKit.Server.Extensions;
using Apiext.MediaKit.Server.Persistence;

// 内容根固定为程序目录：Windows 服务的工作目录是 C:\Windows\System32，
// 不显式指定会让 appsettings.json / APP_DATA / wwwroot 全部找错位置（容器内 /app 下行为不变）。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// 监听地址：默认 0.0.0.0（任意网卡）。没有显式配置 Urls / ASPNETCORE_URLS（命令行 --urls 也算）时，
// Kestrel 会回落 http://localhost:5000 只监听回环，别的机器就访问不到 —— 这里显式兜底成任意 IP。
var configuredUrls = builder.Configuration["Urls"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? Environment.GetEnvironmentVariable("DOTNET_URLS");
if (string.IsNullOrWhiteSpace(configuredUrls))
{
    var listenPort = builder.Configuration.GetValue("App:PublicPort", 28080);
    builder.WebHost.UseUrls($"http://0.0.0.0:{listenPort}");
}

// 以 Windows 服务方式运行时接管生命周期（停止服务能优雅退出、日志进事件查看器）；其它平台无副作用
builder.Host.UseWindowsService(options => options.ServiceName = "Apiext-MediaKit");

// 注册全部服务（Blazor / AntDesign / EF Core / ZLM / 域服务 / 后台服务 / 认证）
builder.Services.AddApiextMediaKit(builder.Configuration);

var app = builder.Build();

// 启动摘要 + 开箱自检：一眼看清「连的哪个 ZLM / 对外地址是什么 / 哪些还是默认值」
app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Apiext.MediaKit.Server.Startup");
    var cfg = app.Configuration;

    using var scope = app.Services.CreateScope();
    var zlm = scope.ServiceProvider.GetRequiredService<Apiext.MediaKit.Server.Services.ZlmService>();
    var sip = app.Services.GetRequiredService<Apiext.MediaKit.Server.Gb28181.GbSipEngine>();

    log.LogInformation("[Startup] Apiext MediaKit 已启动 环境={Env} 监听={Urls}",
        app.Environment.EnvironmentName, string.Join(", ", app.Urls));
    log.LogInformation("[Startup] ZLM API={Api} 播放基址={PlayBase}（{Source}，主机={Host}）",
        zlm.BaseUrl, zlm.PlayBaseUrl, zlm.PlayBaseUrlSource, zlm.PublicHost);
    // 打印真实连接串（Windows 原生部署的库在安装目录 APP_DATA，不再由 ContentRoot 推导，避免日志显示与实际不符）
    log.LogInformation("[Startup] SIP={SipId}@{SipIp}:{SipPort} 媒体地址={MediaIp} DB={Db}",
        cfg["Gb28181:SipId"] ?? "-",
        sip.LocalIp,
        cfg["Gb28181:SipPort"] ?? "28060",
        sip.MediaIp,
        cfg.GetConnectionString("Default") ?? "-");

    // ---- 默认值自检（只提示，不阻断） ----
    const string defaultZlmSecret = "7f3a9c2e-1b4d-4e8f-9a5c-3d6e2b7a1c8f";
    const string defaultHookSecret = "ccd43e63166c6c328ffac037d985117035c64889746d2ff360b1858d8bfb9631";

    if ((cfg["ZlMediaKit:Secret"] ?? "") == defaultZlmSecret)
        log.LogWarning("[Startup] ZlMediaKit:Secret 仍为内置默认值，生产环境请更换（同时改 config.ini 的 [api] secret）");
    if ((cfg["ZlmHook:CapabilitySecret"] ?? "") == defaultHookSecret)
        log.LogWarning("[Startup] ZlmHook:CapabilitySecret 仍为内置默认值，生产环境请更换，并重算 config.ini 中 17 个 hook URL 的 ?cap=");
    if ((cfg["PlayToken:Secret"] ?? "").Length < 32)
        log.LogWarning("[Startup] PlayToken:Secret 未配置或不足 32 字节，播放令牌鉴权已关闭（直连地址不再校验）");
    if (zlm.PublicHostAutoDetected && Apiext.MediaKit.Server.Services.NetworkInfo.IsInContainer())
        log.LogWarning("[Startup] 播放基址使用了自动探测的容器地址 {Host}，请在 compose 中设置 ZlMediaKit__PublicHost=宿主机IP", zlm.PublicHost);
    if (sip.MediaIp.StartsWith("172.", StringComparison.Ordinal) && Apiext.MediaKit.Server.Services.NetworkInfo.IsInContainer())
        log.LogWarning("[Startup] 国标媒体地址 {MediaIp} 看起来是容器地址，设备无法推流；请设置 Gb28181__MediaIp=宿主机IP（或 ZLM 主机 IP）", sip.MediaIp);
});

// 中间件管道 + 端点映射
app.UseApiextMediaKit();

// 确保 SQLite 数据目录存在
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "APP_DATA"));

// 初始化数据库 + 默认管理员
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    DbInitializer.Initialize(db, app.Configuration);
}

app.Run();
