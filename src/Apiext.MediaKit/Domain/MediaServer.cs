using Volo.Abp.Domain.Entities.Auditing;

namespace Apiext.MediaKit.Domain;

public sealed class MediaServer : FullAuditedAggregateRoot<Guid>
{
    public string Name { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;
    public string Secret { get; private set; } = string.Empty;
    public bool IsEnabled { get; private set; }

    private MediaServer() { }

    public MediaServer(Guid id, string name, string baseUrl, string secret, bool isEnabled = true) : base(id)
    {
        CreationTime = DateTime.UtcNow;
        Update(name, baseUrl, secret, isEnabled);
    }

    public void Update(string name, string baseUrl, string secret, bool isEnabled)
    {
        Name = name.Trim();
        BaseUrl = baseUrl.Trim().TrimEnd('/');
        Secret = secret;
        IsEnabled = isEnabled;
    }
}
