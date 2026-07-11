using Volo.Abp.Domain.Entities;

namespace Apiext.MediaKit.Domain;

public sealed class WebhookEvent : AggregateRoot<Guid>
{
    public string EventName { get; private set; } = string.Empty;
    public Guid? MediaServerId { get; private set; }
    public string? Vhost { get; private set; }
    public string? App { get; private set; }
    public string? Stream { get; private set; }
    public string Payload { get; private set; } = "{}";
    public DateTime ReceivedAt { get; private set; }

    private WebhookEvent() { }

    public WebhookEvent(Guid id, string eventName, Guid? mediaServerId, string? vhost,
        string? app, string? stream, string payload) : base(id)
    {
        EventName = eventName;
        MediaServerId = mediaServerId;
        Vhost = vhost;
        App = app;
        Stream = stream;
        Payload = payload;
        ReceivedAt = DateTime.UtcNow;
    }
}
