using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Apiext.MediaKit.Server.Onvif;

/// <summary>WS-Discovery 发现的 ONVIF 设备。</summary>
public class OnvifDiscoveredDevice
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 80;
    public string ServiceUrl { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Scopes { get; set; } = string.Empty;
}

/// <summary>ONVIF WS-Discovery 设备发现（UDP 组播 Probe）。</summary>
public static class OnvifDiscovery
{
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 3702;

    private static readonly string ProbeXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
        "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
        "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
        "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
        "<e:Header>" +
        $"<w:MessageID>uuid:{Guid.NewGuid():N}</w:MessageID>" +
        "<w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
        "<w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>" +
        "</e:Header>" +
        "<e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>" +
        "</e:Envelope>";

    /// <summary>发送 Probe 并收集局域网内的 ONVIF 设备。</summary>
    public static async Task<List<OnvifDiscoveredDevice>> DiscoverAsync(int timeoutSeconds = 3)
    {
        var devices = new Dictionary<string, OnvifDiscoveredDevice>(StringComparer.OrdinalIgnoreCase);

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        var probeBytes = Encoding.UTF8.GetBytes(ProbeXml);
        await udp.SendAsync(probeBytes, probeBytes.Length, new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort));

        udp.Client.ReceiveTimeout = 500;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync();
            }
            catch (SocketException)
            {
                continue; // 500ms 无响应，继续等待到 deadline
            }

            var xml = Encoding.UTF8.GetString(result.Buffer);
            foreach (var device in ParseProbeMatches(xml))
            {
                if (!string.IsNullOrEmpty(device.ServiceUrl))
                {
                    devices.TryAdd(device.ServiceUrl, device);
                }
            }
        }

        return devices.Values.ToList();
    }

    private static IEnumerable<OnvifDiscoveredDevice> ParseProbeMatches(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            yield break;
        }

        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var xaddrs = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value ?? string.Empty;
            var scopes = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value ?? string.Empty;

            var url = xaddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(url)) continue;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

            yield return new OnvifDiscoveredDevice
            {
                ServiceUrl = url,
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 80,
                Scopes = scopes,
                Name = ExtractName(scopes),
            };
        }
    }

    private static string ExtractName(string scopes)
    {
        const string prefix = "onvif://www.onvif.org/name/";
        foreach (var scope in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(scope[prefix.Length..]);
            }
        }
        return string.Empty;
    }
}
