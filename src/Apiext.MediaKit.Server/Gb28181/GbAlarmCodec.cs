using System.Xml.Linq;
using Apiext.MediaKit.Server.Persistence;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>GB28181 报警（Alarm）XML 编解码。</summary>
public static class GbAlarmCodec
{
    /// <summary>构造报警订阅请求体（Query/Alarm）。</summary>
    public static string BuildSubscribeXml(string sn, string deviceId)
    {
        return "<?xml version=\"1.0\"?>" +
               "<Query>" +
               "<CmdType>Alarm</CmdType>" +
               $"<SN>{sn}</SN>" +
               $"<DeviceID>{deviceId}</DeviceID>" +
               "<StartAlarmPriority>1</StartAlarmPriority>" +
               "<EndAlarmPriority>4</EndAlarmPriority>" +
               "<AlarmMethod>5</AlarmMethod>" +
               "</Query>";
    }

    /// <summary>解析报警通知（Notify/Alarm）。</summary>
    public static GbAlarm? ParseAlarmNotify(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }

        var deviceId = doc.Descendants("DeviceID").FirstOrDefault()?.Value ?? string.Empty;
        var alarmTime = doc.Descendants("AlarmTime").FirstOrDefault()?.Value ?? string.Empty;

        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(alarmTime)) return null;

        return new GbAlarm
        {
            DeviceId = deviceId,
            AlarmPriority = doc.Descendants("AlarmPriority").FirstOrDefault()?.Value ?? string.Empty,
            AlarmMethod = doc.Descendants("AlarmMethod").FirstOrDefault()?.Value ?? string.Empty,
            AlarmTime = DateTime.TryParse(alarmTime, out var t) ? t : DateTime.Now,
            AlarmDescription = doc.Descendants("AlarmDescription").FirstOrDefault()?.Value ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
        };
    }
}
