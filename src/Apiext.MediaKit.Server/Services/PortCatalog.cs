namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 对外开放端口清单推导（首页初始化向导与「高级配置 → 端口清单」共用）。
/// 端口全部来自配置（默认非常用端口），改端口后界面展示自动跟随，避免"界面写的端口和实际不一致"。
/// </summary>
public static class PortCatalog
{
    /// <summary>端口清单行。</summary>
    public class PortRow
    {
        public string Name { get; set; } = "";
        public string Port { get; set; } = "";
        public string Protocol { get; set; } = "";
    }

    private static int Get(IConfiguration config, string key, int fallback)
        => int.TryParse(config[key], out var v) && v > 0 ? v : fallback;

    /// <summary>按配置推导对外开放的端口清单。</summary>
    public static List<PortRow> Build(IConfiguration config)
    {
        // 管理平台对外端口（默认 28080：容器版宿主 28080 → 容器内 8080，Windows 原生直接监听 28080）
        var appPort = Get(config, "App:PublicPort", 28080);

        // ZLM HTTP：优先取 ZLM API 基址端口（同机 1:1 部署时即对外端口），再回落到 PublicHttpPort
        var zlmPort = 28180;
        if (Uri.TryCreate(config["ZlMediaKit:BaseUrl"], UriKind.Absolute, out var apiUri) && apiUri.Port > 0)
        {
            zlmPort = apiUri.Port;
        }
        var zlmHttpPort = Get(config, "ZlMediaKit:PublicHttpPort", zlmPort);

        var rtsp = Get(config, "ZlMediaKit:RtspPort", 28554);
        var rtmp = Get(config, "ZlMediaKit:RtmpPort", 28335);
        var webrtc = Get(config, "ZlMediaKit:WebRtcPort", 28000);
        var srt = Get(config, "ZlMediaKit:SrtPort", 29000);
        var sipPort = Get(config, "Gb28181:SipPort", 28060);
        var rtpMin = Get(config, "Gb28181:RtpPortMin", 24000);
        var rtpMax = Get(config, "Gb28181:RtpPortMax", 24500);

        return new List<PortRow>
        {
            new() { Name = "管理平台（HTTP）【必选】", Port = appPort.ToString(), Protocol = "TCP" },
            new() { Name = "ZLMediaKit HTTP（FLV/HLS/WebRTC 信令）【必选】", Port = zlmHttpPort.ToString(), Protocol = "TCP" },
            new() { Name = "RTSP 播放【可选】", Port = rtsp.ToString(), Protocol = "TCP" },
            new() { Name = "RTMP 播放【可选】", Port = rtmp.ToString(), Protocol = "TCP" },
            new() { Name = "WebRTC 媒体【可选】", Port = webrtc.ToString(), Protocol = "TCP & UDP" },
            new() { Name = "SRT 推流【可选】", Port = srt.ToString(), Protocol = "UDP" },
            new() { Name = "GB28181 RTP 收流【必选】", Port = $"{rtpMin}~{rtpMax}", Protocol = "TCP & UDP" },
            new() { Name = "GB28181 SIP 信令【必选】", Port = sipPort.ToString(), Protocol = "UDP" },
        };
    }
}
