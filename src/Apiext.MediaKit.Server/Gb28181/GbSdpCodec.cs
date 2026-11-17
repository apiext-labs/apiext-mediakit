namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>GB28181 实时点播 SDP 编解码。</summary>
public static class GbSdpCodec
{
    /// <summary>
    /// 构造点播 INVITE 的 SDP（平台作为接收方）。
    /// 必须带 PS/90000 等 rtpmap：不少设备（含国标模拟器）缺 rtpmap 会直接回 500。
    /// </summary>
    public static string BuildInviteSdp(string platformId, string platformIp, int rtpPort, string ssrc)
    {
        return "v=0\r\n" +
               $"o={platformId} 0 0 IN IP4 {platformIp}\r\n" +
               "s=Play\r\n" +
               $"c=IN IP4 {platformIp}\r\n" +
               "t=0 0\r\n" +
               $"m=video {rtpPort} RTP/AVP 96 97 98 99\r\n" +
               "a=recvonly\r\n" +
               "a=rtpmap:96 PS/90000\r\n" +
               "a=rtpmap:97 MPEG4/90000\r\n" +
               "a=rtpmap:98 H264/90000\r\n" +
               "a=rtpmap:99 H265/90000\r\n" +
               $"y={ssrc}\r\n" +
               "f=\r\n";
    }

    /// <summary>构造录像回放 INVITE 的 SDP（t= 行为录像起止 Unix 秒时间戳）。</summary>
    public static string BuildPlaybackSdp(string platformId, string platformIp, int rtpPort, string ssrc, DateTime start, DateTime end)
    {
        var startTs = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Local)).ToUnixTimeSeconds();
        var endTs = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Local)).ToUnixTimeSeconds();
        return "v=0\r\n" +
               $"o={platformId} 0 0 IN IP4 {platformIp}\r\n" +
               "s=Playback\r\n" +
               $"c=IN IP4 {platformIp}\r\n" +
               $"t={startTs} {endTs}\r\n" +
               $"m=video {rtpPort} RTP/AVP 96 97 98 99\r\n" +
               "a=recvonly\r\n" +
               "a=rtpmap:96 PS/90000\r\n" +
               "a=rtpmap:97 MPEG4/90000\r\n" +
               "a=rtpmap:98 H264/90000\r\n" +
               "a=rtpmap:99 H265/90000\r\n" +
               $"y={ssrc}\r\n" +
               "f=\r\n";
    }

    /// <summary>解析 SDP 的 y= 字段（SSRC）。</summary>
    public static string? ParseSsrc(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return null;
        foreach (var line in sdp.Split('\n'))
        {
            var l = line.TrimEnd('\r').Trim();
            if (l.StartsWith("y=", StringComparison.OrdinalIgnoreCase))
                return l[2..].Trim();
        }
        return null;
    }

    /// <summary>解析 SDP 的连接地址（c= IN IP4 x.x.x.x）。</summary>
    public static string? ParseConnectionAddress(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return null;
        foreach (var line in sdp.Split('\n'))
        {
            var l = line.TrimEnd('\r').Trim();
            if (l.StartsWith("c=", StringComparison.OrdinalIgnoreCase))
            {
                // c=IN IP4 <ip>
                var parts = l[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3) return parts[2];
            }
        }
        return null;
    }

    /// <summary>解析 SDP 的媒体端口（m=video <port> RTP/AVP ...）。</summary>
    public static int? ParseMediaPort(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp)) return null;
        foreach (var line in sdp.Split('\n'))
        {
            var l = line.TrimEnd('\r').Trim();
            if (l.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = l[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var port))
                    return port;
            }
        }
        return null;
    }
}
