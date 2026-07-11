using Volo.Abp.Domain.Entities.Auditing;

namespace Apiext.MediaKit.Domain;

/// <summary>
/// Stream re-publish (addStreamPusherProxy): source stream on ZLM → remote RTSP/RTMP.
/// </summary>
public sealed class PushProxy : FullAuditedAggregateRoot<Guid>
{
    public Guid MediaServerId { get; private set; }
    public string Schema { get; private set; } = "rtsp";
    public string Vhost { get; private set; } = "__defaultVhost__";
    public string App { get; private set; } = "live";
    public string Stream { get; private set; } = string.Empty;
    public string DestinationUrl { get; private set; } = string.Empty;
    public string Remark { get; private set; } = string.Empty;
    public string? ProxyKey { get; private set; }
    public int RetryCount { get; private set; } = -1;
    public int TimeoutSeconds { get; private set; } = 10;
    public int RtpType { get; private set; }
    public bool IsActive { get; private set; }

    private PushProxy() { }

    public PushProxy(Guid id, Guid mediaServerId, string schema, string vhost, string app, string stream,
        string destinationUrl, string remark, int retryCount, int timeoutSeconds, int rtpType) : base(id)
    {
        MediaServerId = mediaServerId;
        Update(schema, vhost, app, stream, destinationUrl, remark, retryCount, timeoutSeconds, rtpType);
    }

    public void Update(string schema, string vhost, string app, string stream, string destinationUrl,
        string remark, int retryCount, int timeoutSeconds, int rtpType)
    {
        Schema = string.IsNullOrWhiteSpace(schema) ? "rtsp" : schema.Trim().ToLowerInvariant();
        Vhost = string.IsNullOrWhiteSpace(vhost) ? "__defaultVhost__" : vhost.Trim();
        App = app.Trim();
        Stream = stream.Trim();
        DestinationUrl = destinationUrl.Trim();
        Remark = (remark ?? string.Empty).Trim();
        RetryCount = retryCount;
        TimeoutSeconds = timeoutSeconds;
        RtpType = rtpType;
    }

    public void MarkStarted(string? proxyKey)
    {
        ProxyKey = proxyKey;
        IsActive = true;
    }

    public void MarkStopped()
    {
        IsActive = false;
        ProxyKey = null;
    }
}
