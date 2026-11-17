namespace Apiext.MediaKit.Server.Services;

/// <summary>常见厂商 RTSP 地址规则速查（「添加流 → 常见 RTSP 规则」点击填入源流地址用）。</summary>
public static class RtspRuleCatalog
{
    /// <summary>厂商 → 地址模板列表。</summary>
    public static readonly (string Vendor, string[] Items)[] Rules =
    {
        ("海康（常见）", new[]
        {
            "rtsp://admin:admin@192.168.10.199:554/Streaming/Channels/101（主码）",
            "rtsp://admin:admin@192.168.10.199:554/Streaming/Channels/102（子码）",
            "rtsp://admin:admin@192.168.10.199:554/h264/ch1/main/av_stream",
        }),
        ("海康（部分版本支持）", new[]
        {
            "rtsp://admin:admin@192.168.10.199:554/1",
            "rtsp://192.168.10.199:554",
            "rtsp://192.168.10.199:554/Streaming/Channels/101?transportmode=unicast&profile=Profile_1",
        }),
        ("大华", new[]
        {
            "rtsp://admin:admin@192.168.10.199:554/cam/realmonitor?channel=1&subtype=0（主码）",
            "rtsp://admin:admin@192.168.10.199:554/cam/realmonitor?channel=1&subtype=1（子码）",
        }),
        ("宇视", new[]
        {
            "rtsp://admin:admin@192.168.10.199:554/video1（主码）",
            "rtsp://admin:admin@192.168.10.199:554/video2（子码）",
            "rtsp://admin:admin@192.168.10.199:554/media/video1",
            "rtsp://192.168.10.199:554/unicast/c10/s0/live",
            "rtsp://admin:admin@192.168.10.199:554/unicast/c10/s0/live",
        }),
        ("中威", new[] { "rtsp://admin:admin@192.168.10.199:554/Streaming/Channels/1" }),
        ("三星", new[]
        {
            "rtsp://admin:admin@192.168.10.199:554/onvif/profile2/media.smp（高码流单播）",
            "rtsp://admin:admin@192.168.10.199:554/onvif/profile3/media.smp（低码流单播）",
            "rtsp://admin:admin@192.168.10.199:554/onvif/multicast/profile2/media.smp（高码流组播）",
            "rtsp://admin:admin@192.168.10.199:554/onvif/multicast/profile3/media.smp（低码流组播）",
        }),
        ("英飞拓", new[]
        {
            "rtsp://admin:admin@192.168.10.199/1/1080p（高码流，单播/组播一致）",
            "rtsp://admin:admin@192.168.10.199/1/D1（低码流，单播/组播一致）",
            "rtsp://192.168.10.199:554/1/h264major（高码流单播）",
            "rtsp://192.168.10.199:554/1/h264minor（低码流单播）",
            "rtsp://192.168.10.199:554/1/h264major/multicast（高码流组播）",
            "rtsp://192.168.10.199:554/1/h264minor/multicast（低码流组播）",
        }),
        ("LG", new[]
        {
            "rtsp://admin:admin@192.168.10.199:554/Master-0（高码流）",
            "rtsp://admin:admin@192.168.10.199:554/Slave-0（低码流）",
        }),
        ("派尔高", new[]
        {
            "rtsp://192.168.10.199/h264",
            "rtsp://192.168.10.199/h264_2（第一码流）",
        }),
        ("安迅士 / AXIS", new[]
        {
            "rtsp://admin:admin@192.168.10.199/axis-media/media.amp?videocodec=h264&resolution=1280x720&fps=25（单播）",
            "rtsp://admin:admin@192.168.10.199/onvif-media/media.amp?profile=profile_1_h264&streamtype=multicast（组播）",
            "rtsp://root:SPsxt.2020@172.16.8.199/axis-media/media.amp",
        }),
        ("非凡", new[] { "rtsp://admin:admin@192.168.10.199:554/streaming/channels/101" }),
        ("金三立", new[] { "rtsp://admin:admin@192.168.10.199/stream/av0_0" }),
        ("浪潮", new[] { "rtsp://192.168.10.199/live.sdp" }),
        ("Activecam", new[]
        {
            "rtsp://192.168.10.199:554/live/main（主码）",
            "rtsp://192.168.10.199:554/live/sub（子码）",
        }),
        ("华为", new[]
        {
            "rtsp://admin:admin@192.168.10.199:554/LiveMedia/ch1/Media2",
            "rtsp://192.168.10.199:554/stream0?username=admin&password=16位MD5",
        }),
        ("雄迈", new[]
        {
            "rtsp://192.168.10.199:554/user=admin&password=&channel=1&stream=0.sdp?real_stream（主码）",
            "rtsp://192.168.10.199:554/user=admin&password=&channel=1&stream=1.sdp?real_stream（子码）",
        }),
        ("TP-LINK", new[]
        {
            "rtsp://admin:a1234567@192.168.10.199:554/stream1（主码）",
            "rtsp://admin:a1234567@192.168.10.199:554/stream2（子码）",
        }),
        ("华视安邦", new[]
        {
            "rtsp://192.168.10.199:554/live/0/MAIN（主码）",
            "rtsp://192.168.10.199:554/live/0/SUB（子码）",
        }),
        ("科达", new[]
        {
            "rtsp://admin:HCY1.414@172.168.10.92:554/id=0（V5 版本）",
            "rtsp://admin:HCY1.414@172.168.10.92:554/realtime?id=0（V7 版本）",
            "rtsp://admin:HCY1.414@172.168.10.92:554/realtime?id=1（V7 版本）",
            "rtsp://admin:HCY1.414@172.168.10.92:554/realtime?id=0;aid=0;agent=onvif（V7 版本）",
            "rtsp://admin:admin1234567@100.100.71.1/stream5&channel1",
        }),
    };
}
