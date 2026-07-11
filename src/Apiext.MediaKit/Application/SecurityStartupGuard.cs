using Microsoft.Extensions.Options;

namespace Apiext.MediaKit;

public static class SecurityStartupGuard
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var password = configuration["Authentication:Password"] ?? string.Empty;
        var options = configuration.GetSection(MediaKitOptions.SectionName).Get<MediaKitOptions>() ?? new MediaKitOptions();
        var weak = options.Security.WeakPasswords ?? [];
        var isWeak = weak.Any(w => string.Equals(w ?? string.Empty, password, StringComparison.Ordinal));

        if (isWeak)
        {
            var message = "管理员密码为弱口令（如 change-me）。请修改 Authentication:Password。";
            if (options.Security.BlockWeakAdminPassword || environment.IsProduction())
                throw new InvalidOperationException(message + " （Production / BlockWeakAdminPassword=true）");
            logger.LogWarning("SECURITY: {Message}", message);
        }

        if (string.IsNullOrWhiteSpace(options.WebhookSecret) ||
            string.Equals(options.WebhookSecret, "change-webhook-secret", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("SECURITY: MediaKit:WebhookSecret 仍是默认值，生产环境请修改。");
        }

        if (options.StreamAuth.Enabled &&
            (string.IsNullOrWhiteSpace(options.StreamAuth.SigningKey) ||
             string.Equals(options.StreamAuth.SigningKey, "change-stream-auth-key", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning("SECURITY: MediaKit:StreamAuth:SigningKey 仍是默认值，生产环境请修改。");
        }
    }
}
