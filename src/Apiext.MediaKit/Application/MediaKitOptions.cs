namespace Apiext.MediaKit;

public sealed class MediaKitOptions
{
    public const string SectionName = "MediaKit";

    public string WebhookSecret { get; set; } = string.Empty;
    public StreamAuthOptions StreamAuth { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public WebhookSecurityOptions Webhook { get; set; } = new();
    public PlayUrlOptions PlayUrl { get; set; } = new();
    public HealthCheckOptions Health { get; set; } = new();
}

public sealed class StreamAuthOptions
{
    public bool Enabled { get; set; } = true;
    public int TokenTtlMinutes { get; set; } = 60;
    public string SigningKey { get; set; } = "change-stream-auth-key";
    /// <summary>When true and ASPNETCORE_ENVIRONMENT=Development, missing token is allowed (local debug).</summary>
    public bool AllowEmptyTokenInDevelopment { get; set; } = true;
}

public sealed class SecurityOptions
{
    /// <summary>If true, refuse to start when admin password is weak (recommended for Production).</summary>
    public bool BlockWeakAdminPassword { get; set; }
    public string[] WeakPasswords { get; set; } = ["change-me", "admin", "password", "123456", ""];
}

public sealed class WebhookSecurityOptions
{
    /// <summary>Empty = allow any IP. Supports exact IP or CIDR (e.g. 10.0.0.0/8, 192.168.1.0/24).</summary>
    public string[] AllowedIps { get; set; } = [];
    /// <summary>Max webhook requests per IP per window. 0 = disabled.</summary>
    public int RateLimitPerMinute { get; set; } = 120;
}

public sealed class PlayUrlOptions
{
    /// <summary>Override host in generated play URLs (public IP / domain). Null = use MediaServer.BaseUrl host.</summary>
    public string? PublicHost { get; set; }
    /// <summary>HTTP(S) port for FLV/HLS. Null = use BaseUrl port.</summary>
    public int? HttpPort { get; set; }
    public int? HttpsPort { get; set; }
    public int RtspPort { get; set; } = 554;
    public int RtmpPort { get; set; } = 1935;
    /// <summary>Force https/wss in generated HTTP family URLs.</summary>
    public bool ForceHttps { get; set; }
    /// <summary>When vhost is not __defaultVhost__, include it in path as /{vhost}/{app}/{stream}.</summary>
    public bool IncludeCustomVhostInPath { get; set; }
}

public sealed class HealthCheckOptions
{
    /// <summary>When true, /health probes the first enabled MediaServer via getServerConfig.</summary>
    public bool ProbeDefaultZlm { get; set; } = true;
    public int ProbeTimeoutSeconds { get; set; } = 3;
}
