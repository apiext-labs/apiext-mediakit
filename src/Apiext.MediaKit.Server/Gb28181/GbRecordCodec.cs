using System.Xml.Linq;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>录像文件条目（RecordInfo 响应中的单条）。</summary>
public class GbRecordItem
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public string Secrecy { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string RecorderId { get; set; } = string.Empty;
}

/// <summary>GB28181 录像查询（RecordInfo）XML 编解码。</summary>
public static class GbRecordCodec
{
    /// <summary>构造录像查询请求体（Query/RecordInfo）。</summary>
    public static string BuildRecordQueryXml(string sn, string channelId, DateTime start, DateTime end, string type = "all")
    {
        return "<?xml version=\"1.0\"?>" +
               "<Query>" +
               "<CmdType>RecordInfo</CmdType>" +
               $"<SN>{sn}</SN>" +
               $"<DeviceID>{channelId}</DeviceID>" +
               $"<StartTime>{start:yyyy-MM-ddTHH:mm:ss}</StartTime>" +
               $"<EndTime>{end:yyyy-MM-ddTHH:mm:ss}</EndTime>" +
               "<Secrecy>0</Secrecy>" +
               $"<Type>{type}</Type>" +
               "</Query>";
    }

    /// <summary>解析录像列表响应体（Response/RecordInfo/RecordList）。</summary>
    public static List<GbRecordItem> ParseRecordListXml(string xml)
    {
        var items = new List<GbRecordItem>();
        if (string.IsNullOrWhiteSpace(xml)) return items;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return items;
        }

        foreach (var el in doc.Descendants("Item"))
        {
            items.Add(new GbRecordItem
            {
                DeviceId = El(el, "DeviceID"),
                Name = El(el, "Name"),
                FilePath = El(el, "FilePath"),
                Address = El(el, "Address"),
                StartTime = El(el, "StartTime"),
                EndTime = El(el, "EndTime"),
                Secrecy = El(el, "Secrecy"),
                Type = El(el, "Type"),
                RecorderId = El(el, "RecorderID"),
            });
        }

        return items;
    }

    private static string El(XElement parent, string name)
        => parent.Element(name)?.Value ?? string.Empty;
}
