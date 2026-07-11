using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Apiext.MediaKit;

public sealed class StreamAuthService
{
    private readonly StreamAuthOptions _options;
    private readonly IHostEnvironment _environment;

    public StreamAuthService(IOptions<MediaKitOptions> options, IHostEnvironment environment)
    {
        _options = options.Value.StreamAuth;
        _environment = environment;
    }

    public bool Enabled => _options.Enabled;

    public StreamTokenIssueResult IssueToken(string app, string stream, string purpose, int? ttlMinutes = null)
    {
        app = (app ?? string.Empty).Trim();
        stream = (stream ?? string.Empty).Trim();
        purpose = string.IsNullOrWhiteSpace(purpose) ? "play" : purpose.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(stream))
            throw new InvalidOperationException("app/stream are required.");

        var ttl = Math.Clamp(ttlMinutes ?? _options.TokenTtlMinutes, 1, 24 * 60);
        var exp = DateTimeOffset.UtcNow.AddMinutes(ttl).ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var payload = $"{purpose}|{app}|{stream}|{exp}|{nonce}";
        var sig = Sign(payload);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{sig}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new StreamTokenIssueResult(token, exp, purpose, app, stream, ttl);
    }

    public StreamAuthDecision ValidateWebhook(string eventName, JsonElement payload)
    {
        if (!_options.Enabled)
            return StreamAuthDecision.Allow("stream auth disabled");

        // Only gate play / publish hooks.
        var purpose = eventName switch
        {
            "on_play" => "play",
            "on_publish" => "publish",
            _ => null
        };
        if (purpose is null)
            return StreamAuthDecision.Allow("event not gated");

        var app = Read(payload, "app");
        var stream = Read(payload, "stream");
        var token = ReadToken(payload);

        if (string.IsNullOrWhiteSpace(token))
        {
            if (_options.AllowEmptyTokenInDevelopment && _environment.IsDevelopment())
                return StreamAuthDecision.Allow("dev empty token allowed");
            return StreamAuthDecision.Deny("missing stream token");
        }

        if (!TryParseToken(token, out var parsedPurpose, out var parsedApp, out var parsedStream, out var exp, out var nonce, out var sig))
            return StreamAuthDecision.Deny("invalid token format");

        if (!string.Equals(parsedPurpose, purpose, StringComparison.OrdinalIgnoreCase))
            return StreamAuthDecision.Deny("token purpose mismatch");
        if (!string.Equals(parsedApp, app, StringComparison.Ordinal) || !string.Equals(parsedStream, stream, StringComparison.Ordinal))
            return StreamAuthDecision.Deny("token app/stream mismatch");
        if (exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return StreamAuthDecision.Deny("token expired");

        var payloadText = $"{parsedPurpose}|{parsedApp}|{parsedStream}|{exp}|{nonce}";
        var expected = Sign(payloadText);
        if (!FixedEquals(expected, sig))
            return StreamAuthDecision.Deny("bad token signature");

        return StreamAuthDecision.Allow("ok");
    }

    private string Sign(string payload)
    {
        var key = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(_options.SigningKey)
            ? "change-stream-auth-key"
            : _options.SigningKey);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool TryParseToken(string token, out string purpose, out string app, out string stream,
        out long exp, out string nonce, out string sig)
    {
        purpose = app = stream = nonce = sig = string.Empty;
        exp = 0;
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = raw.Split('|');
            if (parts.Length != 6) return false;
            purpose = parts[0];
            app = parts[1];
            stream = parts[2];
            if (!long.TryParse(parts[3], out exp)) return false;
            nonce = parts[4];
            sig = parts[5];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Read(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
            ? v.ToString()
            : string.Empty;

    private static string ReadToken(JsonElement payload)
    {
        // Common ZLM locations: params.token / token / query string fields.
        if (payload.ValueKind != JsonValueKind.Object) return string.Empty;
        if (payload.TryGetProperty("token", out var t1)) return t1.ToString();
        if (payload.TryGetProperty("params", out var paramsEl))
        {
            if (paramsEl.ValueKind == JsonValueKind.Object && paramsEl.TryGetProperty("token", out var t2))
                return t2.ToString();
            if (paramsEl.ValueKind == JsonValueKind.String)
            {
                var qs = paramsEl.GetString() ?? string.Empty;
                foreach (var part in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 2 && kv[0].Equals("token", StringComparison.OrdinalIgnoreCase))
                        return Uri.UnescapeDataString(kv[1]);
                }
            }
        }
        return string.Empty;
    }

    private static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a ?? string.Empty);
        var bb = Encoding.UTF8.GetBytes(b ?? string.Empty);
        if (ba.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}

public sealed record StreamTokenIssueResult(string Token, long ExpiresAtUnix, string Purpose, string App, string Stream, int TtlMinutes);
public sealed record StreamAuthDecision(bool Allowed, string Reason)
{
    public static StreamAuthDecision Allow(string reason) => new(true, reason);
    public static StreamAuthDecision Deny(string reason) => new(false, reason);
}
