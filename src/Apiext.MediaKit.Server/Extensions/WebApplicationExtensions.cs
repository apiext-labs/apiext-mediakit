using Apiext.MediaKit.Server.Components;

namespace Apiext.MediaKit.Server.Extensions;

/// <summary>中间件管道与端点映射收敛。</summary>
public static class WebApplicationExtensions
{
    /// <summary>配置 Apiext MediaKit 中间件管道并映射全部端点。</summary>
    public static void UseApiextMediaKit(this WebApplication app)
    {
        // 请求级日志（最外层，能拿到异常处理后的最终状态码）。
        // 只关注 /api/* 与 /play/*：失败必留痕，API 慢请求（>2s）告警；成功的高频请求不记录，避免刷屏。
        var requestLogger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Apiext.MediaKit.Server.Request");

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? string.Empty;
            var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
            var isPlay = path.StartsWith("/play/", StringComparison.OrdinalIgnoreCase);
            if (!isApi && !isPlay)
            {
                await next();
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await next();
            }
            finally
            {
                sw.Stop();
                var status = ctx.Response.StatusCode;
                var elapsed = sw.ElapsedMilliseconds;
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "-";

                if (status >= 500)
                {
                    requestLogger.LogWarning("[Request] {Method} {Path} 失败 {Status} {Elapsed}ms 来源={Ip}",
                        ctx.Request.Method, path, status, elapsed, ip);
                }
                else if (status >= 400)
                {
                    requestLogger.LogInformation("[Request] {Method} {Path} → {Status} {Elapsed}ms 来源={Ip}",
                        ctx.Request.Method, path, status, elapsed, ip);
                }
                else if (isApi && elapsed > 2000)
                {
                    requestLogger.LogWarning("[Request] 慢请求 {Method} {Path} {Elapsed}ms", ctx.Request.Method, path, elapsed);
                }
            }
        });

        // 播放代理跨域：第三方网页用 hls.js / flv.js 从其它域直接拉 /play/*，
        // 必须回 CORS 头并放行 OPTIONS 预检，否则浏览器会拦掉 m3u8 与分片请求。
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/play"))
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                ctx.Response.Headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
                if (HttpMethods.IsOptions(ctx.Request.Method))
                {
                    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
            }

            await next();
        });

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseStaticFiles();
        app.UseAntiforgery();
        app.UseAuthentication();
        app.UseAuthorization();

        // OpenAPI 文档与 Swagger UI（/api/docs）
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Apiext MediaKit API v1");
            options.RoutePrefix = "api/docs";
        });

        // 全部 HTTP API 统一由 Controllers/ 下的 MVC Controller 提供（/api/*、/play/*）
        app.MapControllers();

        // Blazor 页面（InteractiveServer）
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }
}
