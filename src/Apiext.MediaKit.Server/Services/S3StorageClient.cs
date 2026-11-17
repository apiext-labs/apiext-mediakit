using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 轻量 S3 兼容存储客户端（AWS Signature V4），支持 AWS S3 / MinIO / 阿里云 OSS / 腾讯 COS 等。
/// 仅实现录像备份所需的 HEAD Object / PUT Object 两个操作，不引入第三方 SDK。
/// </summary>
public sealed class S3StorageClient : IDisposable
{
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly string _endpoint;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _bucket;
    private readonly HttpClient _http;

    public S3StorageClient(string endpoint, string region, string accessKey, string secretKey, string bucket)
    {
        _endpoint = endpoint.TrimEnd('/');
        _region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region.Trim();
        _accessKey = accessKey;
        _secretKey = secretKey;
        _bucket = bucket;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>判断对象是否存在；存在返回其长度，不存在返回 null。</summary>
    public async Task<long?> HeadObjectAsync(string key)
    {
        using var req = BuildRequest(HttpMethod.Head, key, EmptyPayloadHash);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        return (int)resp.StatusCode == 200 ? resp.Content.Headers.ContentLength : null;
    }

    /// <summary>上传文件到对象（覆盖式）。</summary>
    public async Task<bool> PutObjectAsync(string key, string localFilePath)
    {
        var payloadHash = await ComputeSha256HexAsync(localFilePath);

        using var req = BuildRequest(HttpMethod.Put, key, payloadHash);
        var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        req.Content = new StreamContent(fs);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        return (int)resp.StatusCode is >= 200 and < 300;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string key, string payloadHash)
    {
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = now.ToString("yyyyMMdd");
        var scope = $"{dateStamp}/{_region}/s3/aws4_request";

        var uri = BuildUri(key);
        var host = uri.Authority;

        var canonicalUri = EncodePath($"/{_bucket}/{key}");
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";

        var canonicalRequest = string.Join('\n',
            method.Method,
            canonicalUri,
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Sha256Hex(canonicalRequest));

        var signature = ComputeSignature(_secretKey, dateStamp, _region, "s3", stringToSign);
        var authorization =
            $"AWS4-HMAC-SHA256 Credential={_accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";

        var req = new HttpRequestMessage(method, uri);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        req.Headers.TryAddWithoutValidation("Authorization", authorization);
        return req;
    }

    private Uri BuildUri(string key)
        => new($"{_endpoint}/{Uri.EscapeDataString(_bucket)}/{EncodePath(key)}");

    private static string EncodePath(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }
        return string.Join('/', segments);
    }

    private static string ComputeSignature(string secretKey, string dateStamp, string region, string service, string stringToSign)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        var kSigning = Hmac(kService, "aws4_request");
        return Hex(Hmac(kSigning, stringToSign));
    }

    private static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Sha256Hex(string data)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await sha.ComputeHashAsync(fs);
        return Hex(hash);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public void Dispose() => _http.Dispose();
}
