namespace Apiext.MediaKit.Server.Services;

/// <summary>流地址协议识别。</summary>
public static class StreamProtocol
{
    /// <summary>从拉流地址推断协议类型（RTSP / RTMP / HTTP-FLV）。</summary>
    public static string Detect(string url)
    {
        if (url.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase)) return "RTMP";
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "HTTP-FLV";
        return "RTSP";
    }
}
