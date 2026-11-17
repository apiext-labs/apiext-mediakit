using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Apiext.MediaKit.Server.Onvif;

/// <summary>ONVIF 设备信息（GetDeviceInformation 结果）。</summary>
public class OnvifDeviceInfo
{
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
}

/// <summary>ONVIF 媒体 Profile（含 PTZ token）。</summary>
public class OnvifProfile
{
    public string ProfileToken { get; set; } = string.Empty;
    public string PtzToken { get; set; } = string.Empty;
}

/// <summary>ONVIF 预置位。</summary>
public class OnvifPreset
{
    public string Token { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>云台移动方向。</summary>
public enum PtzDirection
{
    Up,
    Down,
    Left,
    Right,
    ZoomIn,
    ZoomOut,
}

/// <summary>PTZ 各轴方向反转标记（从 GetConfigurationOptions 的速度空间 Range 解析，Min&gt;Max 表示反转）。</summary>
public class PtzAxes
{
    public bool PanReversed { get; set; }
    public bool TiltReversed { get; set; }
    public bool ZoomReversed { get; set; }
}

/// <summary>
/// ONVIF SOAP 客户端：WS-Security UsernameToken 认证 + GetDeviceInformation / GetProfiles / GetStreamUri。
/// </summary>
public class OnvifClient
{
    private readonly string _deviceServiceUrl;
    private readonly string _mediaServiceUrl;
    private readonly string _defaultPtzServiceUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly HttpClient _http;

    // PTZ 服务地址：默认按标准约定，首次使用时从 capabilities 的 PTZ.XAddr 解析覆盖
    private string _ptzServiceUrl;
    private bool _ptzUrlResolved;
    private readonly SemaphoreSlim _ptzUrlLock = new(1, 1);

    /// <param name="http">可注入的 HttpClient（推荐经 IHttpClientFactory 创建以复用连接）；为 null 时自建。</param>
    public OnvifClient(string host, int port, string username, string password, HttpClient? http = null)
    {
        var baseUrl = $"http://{host}:{port}";
        _deviceServiceUrl = $"{baseUrl}/onvif/device_service";
        _mediaServiceUrl = $"{baseUrl}/onvif/media_service";
        // PTZ 服务地址默认按标准约定（海康/大华等绝大多数设备均为此路径），
        // 首次 PTZ 调用时再尝试从 capabilities 的 PTZ.XAddr 解析覆盖（见 GetPtzServiceUrlAsync）。
        _defaultPtzServiceUrl = $"{baseUrl}/onvif/ptz_service";
        _ptzServiceUrl = _defaultPtzServiceUrl;
        _username = username;
        _password = password;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>获取设备信息（厂商/型号）。</summary>
    public async Task<OnvifDeviceInfo?> GetDeviceInformationAsync()
    {
        var body = "<GetDeviceInformation xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/>";
        var doc = await SendSoapAsync(_deviceServiceUrl, body);
        if (doc == null) return null;

        string Val(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? string.Empty;

        return new OnvifDeviceInfo
        {
            Manufacturer = Val("Manufacturer"),
            Model = Val("Model"),
            FirmwareVersion = Val("FirmwareVersion"),
            SerialNumber = Val("SerialNumber"),
        };
    }

    /// <summary>获取媒体 Profile 列表（返回 profile token）。</summary>
    public async Task<List<string>> GetProfilesAsync()
    {
        var doc = await GetProfilesDocAsync();
        if (doc == null) return new List<string>();

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Profile")
            .Select(e => e.Attribute("token")?.Value)
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .ToList();
    }

    /// <summary>获取媒体 Profile 列表（含 PTZ 配置 token）。</summary>
    public async Task<List<OnvifProfile>> GetProfilesWithPtzAsync()
    {
        var doc = await GetProfilesDocAsync();
        if (doc == null) return new List<OnvifProfile>();

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Profile")
            .Select(e =>
            {
                var ptz = e.Descendants().FirstOrDefault(d => d.Name.LocalName == "PTZConfiguration");
                return new OnvifProfile
                {
                    ProfileToken = e.Attribute("token")?.Value ?? string.Empty,
                    PtzToken = ptz?.Attribute("token")?.Value ?? string.Empty,
                };
            })
            .Where(p => !string.IsNullOrEmpty(p.ProfileToken))
            .ToList();
    }

    private async Task<XDocument?> GetProfilesDocAsync()
    {
        var body = "<GetProfiles xmlns=\"http://www.onvif.org/ver10/media/wsdl\"/>";
        return await SendSoapAsync(_mediaServiceUrl, body);
    }

    /// <summary>获取指定 Profile 的 RTSP 流地址。</summary>
    public async Task<string?> GetStreamUriAsync(string profileToken)
    {
        var body =
            "<GetStreamUri xmlns=\"http://www.onvif.org/ver10/media/wsdl\">" +
            "<StreamSetup>" +
            "<Stream xmlns=\"http://www.onvif.org/ver10/schema\">RTP-Unicast</Stream>" +
            "<Transport xmlns=\"http://www.onvif.org/ver10/schema\"><Protocol>RTSP</Protocol></Transport>" +
            "</StreamSetup>" +
            $"<ProfileToken>{profileToken}</ProfileToken>" +
            "</GetStreamUri>";

        var doc = await SendSoapAsync(_mediaServiceUrl, body);
        if (doc == null) return null;

        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value;
    }

    /// <summary>获取指定 Profile 的 JPEG 抓拍地址（Media GetSnapshotUri）。</summary>
    public async Task<string?> GetSnapshotUriAsync(string profileToken)
    {
        var body =
            "<GetSnapshotUri xmlns=\"http://www.onvif.org/ver10/media/wsdl\">" +
            $"<ProfileToken>{profileToken}</ProfileToken>" +
            "</GetSnapshotUri>";

        var doc = await SendSoapAsync(_mediaServiceUrl, body);
        if (doc == null) return null;

        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value;
    }

    /// <summary>获取设备能力（Device GetCapabilities）。</summary>
    public async Task<XDocument?> GetCapabilitiesAsync()
    {
        var body = "<GetCapabilities xmlns=\"http://www.onvif.org/ver10/device/wsdl\"><Category>All</Category></GetCapabilities>";
        return await SendSoapAsync(_deviceServiceUrl, body);
    }

    /// <summary>解析 PTZ 服务地址：优先从 capabilities 的 PTZ.XAddr 获取，失败回退标准路径。</summary>
    private async Task<string> GetPtzServiceUrlAsync()
    {
        if (_ptzUrlResolved) return _ptzServiceUrl;

        await _ptzUrlLock.WaitAsync();
        try
        {
            if (_ptzUrlResolved) return _ptzServiceUrl;

            var capDoc = await GetCapabilitiesAsync();
            var ptzAddr = capDoc?.Descendants()
                .Where(e => e.Name.LocalName == "PTZ")
                .Select(e => e.Descendants().FirstOrDefault(d => d.Name.LocalName == "XAddr")?.Value)
                .FirstOrDefault(a => !string.IsNullOrEmpty(a));

            if (!string.IsNullOrEmpty(ptzAddr))
            {
                _ptzServiceUrl = ptzAddr;
            }
            _ptzUrlResolved = true;
            return _ptzServiceUrl;
        }
        finally
        {
            _ptzUrlLock.Release();
        }
    }

    /// <summary>获取指定 Profile 对应的 PTZConfiguration token（用于 GetConfigurationOptions）。</summary>
    public async Task<string?> GetPtzConfigurationTokenAsync(string profileToken)
    {
        var doc = await GetProfilesDocAsync();
        if (doc == null) return null;

        var profile = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Profile" && e.Attribute("token")?.Value == profileToken);
        if (profile == null) return null;

        return profile.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "PTZConfiguration")?.Attribute("token")?.Value;
    }

    /// <summary>
    /// 解析设备的 PTZ 速度空间方向（GetConfigurationOptions）。
    /// 返回各轴是否反转（Range 的 Min &gt; Max 表示该轴正向与默认相反），用于云台方向自适应。
    /// </summary>
    public async Task<PtzAxes?> GetPtzAxesAsync(string configToken)
    {
        var body =
            "<GetConfigurationOptions xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
            $"<ConfigurationToken>{configToken}</ConfigurationToken>" +
            "</GetConfigurationOptions>";

        var doc = await SendSoapAsync(await GetPtzServiceUrlAsync(), body);
        if (doc == null) return null;

        var panTilt = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ContinuousPanTiltVelocitySpace");
        var zoom = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ContinuousZoomVelocitySpace");

        double Bound(XElement? space, string axisName, string boundName)
        {
            var range = space?.Elements().FirstOrDefault(e => e.Name.LocalName == axisName);
            var value = range?.Elements().FirstOrDefault(e => e.Name.LocalName == boundName)?.Value;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        return new PtzAxes
        {
            PanReversed = Bound(panTilt, "XRange", "Min") > Bound(panTilt, "XRange", "Max"),
            TiltReversed = Bound(panTilt, "YRange", "Min") > Bound(panTilt, "YRange", "Max"),
            ZoomReversed = Bound(zoom, "XRange", "Min") > Bound(zoom, "XRange", "Max"),
        };
    }

    /// <summary>获取指定 Profile 的预置位列表（PTZ 服务 GetPresets）。</summary>
    public async Task<List<OnvifPreset>> GetPresetsAsync(string profileToken)
    {
        var body =
            "<GetPresets xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
            $"<ProfileToken>{profileToken}</ProfileToken>" +
            "</GetPresets>";

        var doc = await SendSoapAsync(await GetPtzServiceUrlAsync(), body);
        if (doc == null) return new List<OnvifPreset>();

        // 兼容 ver20 的 PTZPreset 与个别设备返回的 Preset 元素
        return doc.Descendants()
            .Where(e => e.Name.LocalName is "PTZPreset" or "Preset")
            .Select(e => new OnvifPreset
            {
                Token = e.Attribute("token")?.Value ?? string.Empty,
                Name = e.Elements().FirstOrDefault(c => c.Name.LocalName == "Name")?.Value ?? string.Empty,
            })
            .Where(p => !string.IsNullOrEmpty(p.Token))
            .ToList();
    }

    /// <summary>保存当前云台位置为新预置位（PTZ 服务 SetPreset，返回新预置位 token）。</summary>
    public async Task<string?> SetPresetAsync(string profileToken, string presetName, string? presetToken = null)
    {
        static string Esc(string s) => s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

        var body =
            "<SetPreset xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
            $"<ProfileToken>{profileToken}</ProfileToken>" +
            $"<PresetName>{Esc(presetName)}</PresetName>" +
            (string.IsNullOrEmpty(presetToken) ? string.Empty : $"<PresetToken>{presetToken}</PresetToken>") +
            "</SetPreset>";

        var doc = await SendSoapAsync(await GetPtzServiceUrlAsync(), body);
        if (doc == null) return null;

        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PresetToken")?.Value;
    }

    /// <summary>删除指定预置位（PTZ 服务 RemovePreset）。</summary>
    public async Task<bool> RemovePresetAsync(string profileToken, string presetToken)
    {
        var body =
            "<RemovePreset xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
            $"<ProfileToken>{profileToken}</ProfileToken>" +
            $"<PresetToken>{presetToken}</PresetToken>" +
            "</RemovePreset>";

        return await SendSoapAsync(await GetPtzServiceUrlAsync(), body) != null;
    }

    /// <summary>跳转到指定预置位（PTZ 服务 GotoPreset）。</summary>
    public async Task<bool> GotoPresetAsync(string profileToken, string presetToken, double speed = 1.0)
    {
        var s = speed.ToString("0.###", CultureInfo.InvariantCulture);
        var body =
            "<GotoPreset xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
            $"<ProfileToken>{profileToken}</ProfileToken>" +
            $"<PresetToken>{presetToken}</PresetToken>" +
            "<Speed>" +
            $"<PanTilt x=\"{s}\" y=\"{s}\" xmlns=\"http://www.onvif.org/ver10/schema\"/>" +
            $"<Zoom x=\"{s}\" xmlns=\"http://www.onvif.org/ver10/schema\"/>" +
            "</Speed>" +
            "</GotoPreset>";

        return await SendSoapAsync(await GetPtzServiceUrlAsync(), body) != null;
    }

    /// <summary>按方向连续移动云台（Pan/Tilt/Zoom 归一化速度，可选按设备轴方向自适应）。</summary>
    public Task<bool> ContinuousMoveAsync(string profileToken, PtzDirection direction, double speed = 0.5, PtzAxes? axes = null)
    {
        var signX = axes?.PanReversed == true ? -1.0 : 1.0;
        var signY = axes?.TiltReversed == true ? -1.0 : 1.0;
        var signZ = axes?.ZoomReversed == true ? -1.0 : 1.0;

        var (px, py, zx) = direction switch
        {
            PtzDirection.Up => (0.0, speed * signY, 0.0),
            PtzDirection.Down => (0.0, -speed * signY, 0.0),
            PtzDirection.Left => (-speed * signX, 0.0, 0.0),
            PtzDirection.Right => (speed * signX, 0.0, 0.0),
            PtzDirection.ZoomIn => (0.0, 0.0, speed * signZ),
            PtzDirection.ZoomOut => (0.0, 0.0, -speed * signZ),
            _ => (0.0, 0.0, 0.0),
        };
        return ContinuousMoveAsync(profileToken, px, py, zx);
    }

    /// <summary>连续云台移动（PTZ 服务 ContinuousMove，速度空间为归一化 -1..1）。</summary>
    public async Task<bool> ContinuousMoveAsync(
        string profileToken, double panTiltX, double panTiltY, double zoomX, double timeoutSeconds = 0.6)
    {
        static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var timeout = $"PT{timeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture)}S";
        var body =
            "<ContinuousMove xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
            $"<ProfileToken>{profileToken}</ProfileToken>" +
            "<Velocity>" +
            $"<PanTilt x=\"{Fmt(panTiltX)}\" y=\"{Fmt(panTiltY)}\" xmlns=\"http://www.onvif.org/ver10/schema\"/>" +
            $"<Zoom x=\"{Fmt(zoomX)}\" xmlns=\"http://www.onvif.org/ver10/schema\"/>" +
            "</Velocity>" +
            $"<Timeout>{timeout}</Timeout>" +
            "</ContinuousMove>";

        return await SendSoapAsync(await GetPtzServiceUrlAsync(), body) != null;
    }

    /// <summary>停止云台移动（PTZ 服务 Stop）。</summary>
    public async Task<bool> StopAsync(string profileToken, bool panTilt = true, bool zoom = true)
    {
        var body =
            "<Stop xmlns=\"http://www.onvif.org/ver20/ptz/wsdl\">" +
            $"<ProfileToken>{profileToken}</ProfileToken>" +
            $"<PanTilt>{(panTilt ? "true" : "false")}</PanTilt>" +
            $"<Zoom>{(zoom ? "true" : "false")}</Zoom>" +
            "</Stop>";

        return await SendSoapAsync(await GetPtzServiceUrlAsync(), body) != null;
    }

    /// <summary>发送 SOAP 请求并返回响应 XML（带 WS-Security 认证）。</summary>
    private async Task<XDocument?> SendSoapAsync(string url, string bodyXml)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var nonceB64 = Convert.ToBase64String(nonce);

        // PasswordDigest = Base64(SHA1(nonce + created + password))
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passwordBytes = Encoding.UTF8.GetBytes(_password);
        var combined = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, combined, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, combined, nonce.Length + createdBytes.Length, passwordBytes.Length);
        var digest = Convert.ToBase64String(SHA1.HashData(combined));

        var security =
            "<Security s:mustUnderstand=\"1\" xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
            "<UsernameToken>" +
            $"<Username>{_username}</Username>" +
            $"<Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">{digest}</Password>" +
            $"<Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">{nonceB64}</Nonce>" +
            $"<Created xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">{created}</Created>" +
            "</UsernameToken>" +
            "</Security>";

        var envelope =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
            "<s:Header>" + security + "</s:Header>" +
            $"<s:Body>{bodyXml}</s:Body>" +
            "</s:Envelope>";

        try
        {
            using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
            using var resp = await _http.PostAsync(url, content);
            var responseText = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return null;

            // 去掉可能的 BOM/前导空白
            var idx = responseText.IndexOf('<');
            if (idx < 0) return null;

            var doc = XDocument.Parse(responseText[idx..]);

            // 检查 SOAP Fault
            var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault != null) return null;

            return doc;
        }
        catch
        {
            return null;
        }
    }
}
