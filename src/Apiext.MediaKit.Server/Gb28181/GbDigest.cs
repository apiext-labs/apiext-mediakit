using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>GB28181 SIP Digest 鉴权（RFC2617，MD5）。</summary>
public static class GbDigest
{
    /// <summary>生成随机 nonce</summary>
    public static string GenerateNonce()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>构造 WWW-Authenticate 挑战头</summary>
    public static string BuildChallenge(string realm, string nonce)
        => $"Digest realm=\"{realm}\", nonce=\"{nonce}\", algorithm=MD5";

    /// <summary>计算 Digest response（无 qop 的国标常见形态）</summary>
    public static string ComputeResponse(string username, string realm, string password, string method, string uri, string nonce)
    {
        var ha1 = Md5Hex($"{username}:{realm}:{password}");
        var ha2 = Md5Hex($"{method}:{uri}");
        return Md5Hex($"{ha1}:{nonce}:{ha2}");
    }

    /// <summary>解析 Authorization 头，返回键值字典（username/realm/nonce/uri/response/algorithm）</summary>
    public static Dictionary<string, string> ParseAuthorization(string header)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(header)) return result;

        var idx = header.IndexOf(' ');
        var body = idx >= 0 ? header[(idx + 1)..] : header;
        foreach (Match m in Regex.Matches(body, "(\\w+)\\s*=\\s*\"([^\"]*)\""))
        {
            result[m.Groups[1].Value] = m.Groups[2].Value;
        }

        return result;
    }

    private static string Md5Hex(string input)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
