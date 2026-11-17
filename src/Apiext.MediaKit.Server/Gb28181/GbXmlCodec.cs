using System.Text;
using System.Xml.Linq;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>GB28181 目录（Catalog）XML 编解码。</summary>
public static class GbXmlCodec
{
    /// <summary>构造目录查询请求体（Query/Catalog）</summary>
    public static string BuildCatalogQuery(string sn, string deviceId)
    {
        return "<?xml version=\"1.0\"?>" +
               "<Query>" +
               $"<CmdType>Catalog</CmdType>" +
               $"<SN>{sn}</SN>" +
               $"<DeviceID>{deviceId}</DeviceID>" +
               "</Query>";
    }

    /// <summary>构造目录响应/推送体（Response/Catalog/DeviceList），用于平台级联向上一级推送通道目录。</summary>
    public static string BuildCatalogResponse(string sn, string deviceId, IEnumerable<GbCatalogItem> items)
    {
        var list = items.ToList();
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>");
        sb.Append("<Response>");
        sb.Append("<CmdType>Catalog</CmdType>");
        sb.Append($"<SN>{sn}</SN>");
        sb.Append($"<DeviceID>{deviceId}</DeviceID>");
        sb.Append($"<SumNum>{list.Count}</SumNum>");
        sb.Append($"<DeviceList Num=\"{list.Count}\">");
        foreach (var it in list)
        {
            sb.Append("<Item>");
            sb.Append($"<DeviceID>{Esc(it.DeviceId)}</DeviceID>");
            sb.Append($"<Name>{Esc(it.Name)}</Name>");
            sb.Append($"<Manufacturer>{Esc(it.Manufacturer)}</Manufacturer>");
            sb.Append($"<Model>{Esc(it.Model)}</Model>");
            sb.Append($"<Owner>{Esc(it.Owner)}</Owner>");
            sb.Append($"<CivilCode>{Esc(it.CivilCode)}</CivilCode>");
            sb.Append($"<Address>{Esc(it.Address)}</Address>");
            sb.Append($"<Parental>{(it.Parental == 1 ? 1 : 0)}</Parental>");
            sb.Append($"<ParentID>{Esc(it.ParentId)}</ParentID>");
            sb.Append($"<Status>{Esc(string.IsNullOrEmpty(it.Status) ? "ON" : it.Status)}</Status>");
            sb.Append("</Item>");
        }
        sb.Append("</DeviceList>");
        sb.Append("</Response>");
        return sb.ToString();
    }

    /// <summary>提取 XML 正文中首个指定标签的文本值（SIP MESSAGE 正文非严格 XML 时的兜底解析）。</summary>
    public static string? ExtractXmlValue(string xml, string tag)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var start = xml.IndexOf($"<{tag}>", StringComparison.Ordinal);
        if (start < 0) return null;
        start += tag.Length + 2;
        var end = xml.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
        if (end < 0) return null;
        return xml[start..end];
    }

    private static string Esc(string s)
        => (s ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

    /// <summary>解析目录响应体（Response/Catalog/DeviceList），返回通道列表。</summary>
    public static List<GbCatalogItem> ParseCatalogResponse(string xml)
    {
        var items = new List<GbCatalogItem>();
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

        foreach (var itemEl in doc.Descendants("Item"))
        {
            items.Add(new GbCatalogItem
            {
                DeviceId = El(itemEl, "DeviceID"),
                Name = El(itemEl, "Name"),
                Manufacturer = El(itemEl, "Manufacturer"),
                Model = El(itemEl, "Model"),
                Owner = El(itemEl, "Owner"),
                CivilCode = El(itemEl, "CivilCode"),
                Address = El(itemEl, "Address"),
                Parental = El(itemEl, "Parental") == "1" ? 1 : 0,
                ParentId = El(itemEl, "ParentID"),
                Status = El(itemEl, "Status"),
            });
        }

        return items;
    }

    private static string El(XElement parent, string name)
        => parent.Element(name)?.Value ?? string.Empty;
}

/// <summary>目录解析出的通道条目。</summary>
public class GbCatalogItem
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string CivilCode { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Parental { get; set; }
    public string ParentId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
