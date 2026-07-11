namespace Apiext.MediaKit.HostServices;

/// <summary>
/// Forwards the browser request's Cookie header onto server-side HttpClient calls
/// (Interactive Server / SSR), so /api/* sees the same auth cookie as the page request.
/// </summary>
public sealed class CookieForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CookieForwardingHandler(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is not null
            && !request.Headers.Contains("Cookie")
            && httpContext.Request.Headers.TryGetValue("Cookie", out var cookie)
            && !string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie.ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
