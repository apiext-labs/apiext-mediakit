using Volo.Abp.Domain.Entities.Auditing;

namespace Apiext.MediaKit.Domain;

public sealed class PullProxy : FullAuditedAggregateRoot<Guid>
{
    public Guid MediaServerId { get; private set; }
    public string Vhost { get; private set; } = "__defaultVhost__";
    public string App { get; private set; } = "live";
    public string Stream { get; private set; } = string.Empty;
    public string SourceUrl { get; private set; } = string.Empty;
    public string SourceUrlsJson { get; private set; } = "[]";
    public string Remark { get; private set; } = string.Empty;
    public string? ProxyKey { get; private set; }
    public Guid? ProtocolPresetId { get; private set; }
    public bool OnDemand { get; private set; }
    public int RetryCount { get; private set; } = -1;
    public int TimeoutSeconds { get; private set; } = 10;
    public int RtpType { get; private set; }
    public bool EnableHls { get; private set; }
    public bool EnableMp4 { get; private set; }
    public bool IsActive { get; private set; }

    private PullProxy() { }

    public PullProxy(Guid id, Guid mediaServerId, string vhost, string app, string stream,
        IReadOnlyCollection<string> sourceUrls, string remark, Guid? protocolPresetId, bool onDemand,
        int retryCount, int timeoutSeconds, int rtpType, bool enableHls, bool enableMp4) : base(id)
    {
        MediaServerId = mediaServerId;
        Vhost = vhost;
        App = app;
        Stream = stream;
        SetSources(sourceUrls);
        Remark = remark.Trim();
        ProtocolPresetId = protocolPresetId;
        OnDemand = onDemand;
        RetryCount = retryCount;
        TimeoutSeconds = timeoutSeconds;
        RtpType = rtpType;
        EnableHls = enableHls;
        EnableMp4 = enableMp4;
    }

    public void MarkStarted(string? proxyKey)
    {
        ProxyKey = proxyKey;
        IsActive = true;
    }

    public void MarkStopped() => IsActive = false;

    public void SetMode(bool onDemand) => OnDemand = onDemand;

    public void UpdateConfig(IReadOnlyCollection<string> sourceUrls, string remark, Guid? protocolPresetId,
        bool onDemand, int retryCount, int timeoutSeconds, int rtpType, bool enableHls, bool enableMp4)
    {
        SetSources(sourceUrls);
        Remark = (remark ?? string.Empty).Trim();
        ProtocolPresetId = protocolPresetId;
        OnDemand = onDemand;
        RetryCount = retryCount;
        TimeoutSeconds = timeoutSeconds;
        RtpType = rtpType;
        EnableHls = enableHls;
        EnableMp4 = enableMp4;
    }

    private void SetSources(IReadOnlyCollection<string> sourceUrls)
    {
        var normalized = sourceUrls.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray();
        SourceUrl = normalized.FirstOrDefault() ?? string.Empty;
        SourceUrlsJson = System.Text.Json.JsonSerializer.Serialize(normalized);
    }
}
