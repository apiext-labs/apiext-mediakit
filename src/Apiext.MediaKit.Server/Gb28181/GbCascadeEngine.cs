using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Apiext.MediaKit.Server.Apis;
using Apiext.MediaKit.Server.Persistence;
using Apiext.MediaKit.Server.Services;
using Microsoft.EntityFrameworkCore;
using SIPSorcery.SIP;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>单个上级平台的运行时状态。</summary>
public class CascadeUpperState
{
    public CascadeUpper Config { get; set; } = new();
    public SIPEndPoint? EndPoint { get; set; }
    public bool Registered { get; set; }
    public string CallId { get; set; } = Guid.NewGuid().ToString("N");
    public int CSeq { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string RealmFromChallenge { get; set; } = string.Empty;

    public string Key => $"{Config.Ip}:{Config.Port}";
}

/// <summary>级联会话：某个上级平台点播的一个通道。</summary>
public class CascadeSession
{
    public string ChannelId { get; set; } = string.Empty;
    public string UpperKey { get; set; } = string.Empty;
    public string UpperSsrc { get; set; } = string.Empty;
    public string UpperIp { get; set; } = string.Empty;
    public int UpperPort { get; set; }
    public string CallId { get; set; } = string.Empty;
    public string FromTag { get; set; } = string.Empty;
    public string ToTag { get; set; } = string.Empty;
    public int CSeq { get; set; }
}

/// <summary>
/// GB28181 平台级联引擎：本平台作为下级域，向一个或多个上级国标平台注册、推送目录，
/// 并响应上级的点播/云台/停止。复用 <see cref="GbSipEngine"/> 的 SIP 传输层；媒体转发走 ZLM startSendRtp。
/// </summary>
public class GbCascadeEngine : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GbCascadeEngine> _logger;
    private readonly GbSipEngine _sip;

    private bool _enabled;
    private string _localId = string.Empty;
    private int _registerInterval = 60;
    private int _reloadInterval = 5;
    private int _sn;

    private readonly ConcurrentDictionary<string, CascadeUpperState> _uppers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CascadeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public GbCascadeEngine(
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        ILogger<GbCascadeEngine> logger,
        GbSipEngine sip)
    {
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _sip = sip;
        ReadConfig();
    }

    private void ReadConfig()
    {
        var section = _config.GetSection("Gb28181:Cascade");
        _enabled = section.GetValue("Enabled", false);
        _localId = section.GetValue("LocalId", string.Empty)!;
        _registerInterval = section.GetValue("RegisterInterval", 60);
        _reloadInterval = section.GetValue("ReloadInterval", 5);

        if (!_enabled) return;
        if (string.IsNullOrWhiteSpace(_localId))
        {
            _logger.LogWarning("[Cascade] 配置不完整（LocalId 必填），级联未启用");
            _enabled = false;
        }
    }

    public bool Enabled => _enabled;
    public string LocalId => _localId;

    /// <summary>各上级平台状态（供 REST API 查询）。</summary>
    public IReadOnlyList<CascadeUpperState> Uppers => _uppers.Values.ToList();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_enabled) return;

        // 让 GbSipEngine 跳过任意上级平台发来的请求（交给本引擎处理）
        _sip.ExternalPeerFilter = remote =>
            _uppers.Values.Any(u =>
                remote.Address.Equals(IPAddress.Parse(u.Config.Ip)) && remote.Port == u.Config.Port);

        // 等待 SIP 传输层就绪
        while (!ct.IsCancellationRequested && _sip.Transport == null)
        {
            await Task.Delay(500, ct);
        }
        var transport = _sip.Transport;
        if (transport == null) return;

        transport.SIPTransportRequestReceived += OnUpperRequestReceived;
        transport.SIPTransportResponseReceived += OnUpperResponseReceived;

        // 首次从 DB 加载上级平台列表
        await ReloadAsync();

        _logger.LogInformation("[Cascade] 启动，本平台编码 {LocalId}，上级平台 {Count} 个",
            _localId, _uppers.Count);

        try
        {
            foreach (var u in _uppers.Values)
            {
                await RegisterAsync(u);
            }

            var reloadEvery = Math.Max(1, _registerInterval / Math.Max(1, _reloadInterval));
            int tick = 0;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_reloadInterval), ct);

                // 周期性同步 DB 中的上级平台增删改
                await ReloadAsync();

                if (++tick >= reloadEvery)
                {
                    tick = 0;
                    foreach (var u in _uppers.Values)
                    {
                        await RegisterAsync(u);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        finally
        {
            transport.SIPTransportRequestReceived -= OnUpperRequestReceived;
            transport.SIPTransportResponseReceived -= OnUpperResponseReceived;
        }
    }

    /// <summary>
    /// 从数据库重新加载上级平台列表并 diff 到运行时状态：
    /// 新增的平台立即建立端点并注册，被删除的平台移除。可由 REST API 在增删改后直接触发。
    /// </summary>
    public async Task ReloadAsync()
    {
        List<CascadeUpper> rows;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            rows = await db.CascadeUppers.OrderBy(u => u.Id).ToListAsync();
        }

        var valid = new Dictionary<string, CascadeUpper>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Ip) || string.IsNullOrWhiteSpace(row.PlatformId)
                || !IPAddress.TryParse(row.Ip, out _))
            {
                _logger.LogWarning("[Cascade] 跳过无效上级配置：{Name}", row.Name);
                continue;
            }
            valid[row.Key] = row;
        }

        // 移除 DB 中已不存在的平台
        foreach (var key in _uppers.Keys.ToList())
        {
            if (!valid.ContainsKey(key))
            {
                _uppers.TryRemove(key, out _);
                _logger.LogInformation("[Cascade] 移除上级平台 {Key}", key);
            }
        }

        // 新增或更新
        foreach (var (key, row) in valid)
        {
            if (_uppers.TryGetValue(key, out var existing))
            {
                existing.Config = row;
            }
            else
            {
                var state = new CascadeUpperState
                {
                    Config = row,
                    EndPoint = new SIPEndPoint(SIPProtocolsEnum.udp,
                        new IPEndPoint(IPAddress.Parse(row.Ip), row.Port)),
                };
                _uppers[key] = state;
                _logger.LogInformation("[Cascade] 新增上级平台 {Name}（{Ip}:{Port}）", row.Name, row.Ip, row.Port);

                if (_enabled && _sip.Transport != null)
                {
                    _ = RegisterAsync(state);
                }
            }
        }
    }

    private CascadeUpperState? FindByRemote(SIPEndPoint remote)
    {
        return _uppers.Values.FirstOrDefault(u =>
            u.EndPoint != null &&
            u.EndPoint.Address.Equals(remote.Address) && u.EndPoint.Port == remote.Port);
    }

    // ---------- 向上级注册（UAC） ----------

    private async Task RegisterAsync(CascadeUpperState u, bool withAuth = false)
    {
        var transport = _sip.Transport;
        if (transport == null || u.EndPoint == null) return;

        var toUri = GbSipMessageFactory.ToUri(u.EndPoint, u.Config.PlatformId);
        var fromUri = GbSipMessageFactory.FromUri(_localId, _sip.LocalIp, _sip.SipPort);
        var req = GbSipMessageFactory.Create(SIPMethodsEnum.REGISTER, toUri, fromUri,
            callId: u.CallId, cseq: ++u.CSeq);
        req.Header.Expires = Math.Max(60, _registerInterval * 3);

        if (withAuth)
        {
            var realm = string.IsNullOrEmpty(u.RealmFromChallenge) ? u.Config.Realm : u.RealmFromChallenge;
            if (string.IsNullOrEmpty(realm)) realm = u.Config.Domain;
            var uri = string.IsNullOrEmpty(u.Config.Domain)
                ? $"sip:{u.Config.PlatformId}" : $"sip:{u.Config.PlatformId}@{u.Config.Domain}";
            var response = GbDigest.ComputeResponse(_localId, realm, u.Config.Password, "REGISTER", uri, u.Nonce);

            var digest = new SIPAuthorisationDigest(SIPAuthorisationHeadersEnum.Authorize)
            {
                Username = _localId,
                Realm = realm,
                Nonce = u.Nonce,
                URI = uri,
                Response = response,
                DigestAlgorithm = DigestAlgorithmsEnum.MD5,
            };
            var authHeader = new SIPAuthenticationHeader(digest);
            req.Header.AuthenticationHeaders = new List<SIPAuthenticationHeader> { authHeader };
        }

        try
        {
            await transport.SendRequestAsync(u.EndPoint, req);
            _logger.LogDebug("[Cascade] 向 {Name} 发送 REGISTER（auth={Auth}）", u.Config.Name, withAuth);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Cascade] 向 {Name} 发送 REGISTER 失败", u.Config.Name);
        }
    }

    private async Task OnUpperResponseReceived(SIPEndPoint local, SIPEndPoint remote, SIPResponse response)
    {
        try
        {
            if (response.Header.CSeqMethod != SIPMethodsEnum.REGISTER) return;
            var u = FindByRemote(remote);
            if (u == null) return;

            if (response.Status == SIPResponseStatusCodesEnum.Unauthorised)
            {
                var authHeader = response.Header.AuthenticationHeaders?.FirstOrDefault();
                if (authHeader != null)
                {
                    u.Nonce = authHeader.SIPDigest.Nonce;
                    u.RealmFromChallenge = authHeader.SIPDigest.Realm;
                    await RegisterAsync(u, withAuth: true);
                }
            }
            else if (response.Status == SIPResponseStatusCodesEnum.Ok)
            {
                if (!u.Registered)
                {
                    u.Registered = true;
                    _logger.LogInformation("[Cascade] 向上级 {Name} 注册成功", u.Config.Name);
                    _ = PushCatalogAsync(u);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Cascade] 处理上级响应异常");
        }
    }

    // ---------- 处理上级请求（UAS） ----------

    private async Task OnUpperRequestReceived(SIPEndPoint local, SIPEndPoint remote, SIPRequest request)
    {
        try
        {
            var u = FindByRemote(remote);
            if (u == null) return;

            switch (request.Method)
            {
                case SIPMethodsEnum.MESSAGE:
                    await ProcessUpperMessageAsync(request, u);
                    break;
                case SIPMethodsEnum.INVITE:
                    await ProcessUpperInviteAsync(request, u);
                    break;
                case SIPMethodsEnum.BYE:
                    await ProcessUpperByeAsync(request);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Cascade] 处理上级请求异常");
        }
    }

    private async Task ProcessUpperMessageAsync(SIPRequest request, CascadeUpperState u)
    {
        var cmdType = GbXmlCodec.ExtractXmlValue(request.Body, "CmdType")?.ToUpperInvariant();
        var sn = GbXmlCodec.ExtractXmlValue(request.Body, "SN") ?? string.Empty;
        var deviceId = GbXmlCodec.ExtractXmlValue(request.Body, "DeviceID") ?? string.Empty;

        switch (cmdType)
        {
            case "CATALOG":
                await SendResponseAsync(request, SIPResponseStatusCodesEnum.Ok);
                _ = PushCatalogAsync(u, sn);
                break;
            case "DEVICECONTROL":
                await SendResponseAsync(request, SIPResponseStatusCodesEnum.Ok);
                var ptzCmd = GbXmlCodec.ExtractXmlValue(request.Body, "PTZCmd");
                if (!string.IsNullOrEmpty(ptzCmd))
                {
                    await ForwardPtzAsync(deviceId, ptzCmd);
                }
                break;
            default:
                await SendResponseAsync(request, SIPResponseStatusCodesEnum.Ok);
                break;
        }
    }

    private async Task ProcessUpperInviteAsync(SIPRequest request, CascadeUpperState u)
    {
        var channelId = request.Header.To.ToURI.User ?? string.Empty;
        var upperIp = GbSdpCodec.ParseConnectionAddress(request.Body);
        var upperPort = GbSdpCodec.ParseMediaPort(request.Body);
        var upperSsrc = GbSdpCodec.ParseSsrc(request.Body);

        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(upperIp) || upperPort == null || string.IsNullOrEmpty(upperSsrc))
        {
            await SendResponseAsync(request, SIPResponseStatusCodesEnum.BadRequest);
            return;
        }

        string? deviceId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            deviceId = (await db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId))?.DeviceId;
        }
        if (string.IsNullOrEmpty(deviceId) || _sip.GetDevice(deviceId) == null)
        {
            await SendResponseAsync(request, SIPResponseStatusCodesEnum.NotFound);
            return;
        }

        var streamId = "cascade_" + channelId;

        OpenRtpServerResult rtp;
        using (var scope = _scopeFactory.CreateScope())
        {
            var zlm = scope.ServiceProvider.GetRequiredService<ZlmService>();
            rtp = await zlm.OpenRtpServerAsync(streamId, 0, 0);
        }
        if (rtp.Code != 0 || rtp.Port <= 0)
        {
            await SendResponseAsync(request, SIPResponseStatusCodesEnum.InternalServerError);
            return;
        }

        await SendInviteOkAsync(request, rtp.Port, upperSsrc);

        var ssrc = "0" + Random.Shared.Next(0, 1_000_000_000).ToString().PadLeft(9, '0');
        var (ok, _) = await _sip.InviteAsync(deviceId, channelId, streamId, rtp.Port, ssrc);
        if (!ok)
        {
            _logger.LogWarning("[Cascade] 向设备点播失败，通道 {Channel}", channelId);
            using (var scope = _scopeFactory.CreateScope())
            {
                var zlm = scope.ServiceProvider.GetRequiredService<ZlmService>();
                try { await zlm.CloseRtpServerAsync(streamId); } catch { }
            }
            return;
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            var zlm = scope.ServiceProvider.GetRequiredService<ZlmService>();
            var send = await zlm.StartSendRtpAsync(streamId, upperSsrc, upperIp, upperPort.Value, 1);
            if (send.Code != 0)
            {
                _logger.LogWarning("[Cascade] RTP 转发失败：{Msg}", send.Msg);
            }
        }

        _sessions[channelId] = new CascadeSession
        {
            ChannelId = channelId,
            UpperKey = u.Key,
            UpperSsrc = upperSsrc,
            UpperIp = upperIp,
            UpperPort = upperPort.Value,
            CallId = request.Header.CallId,
            FromTag = request.Header.From.FromTag,
            ToTag = request.Header.To.ToTag,
            CSeq = request.Header.CSeq,
        };
        _logger.LogInformation("[Cascade] 上级 {Name} 点播通道 {Channel}，转发到 {Ip}:{Port}（SSRC {Ssrc}）",
            u.Config.Name, channelId, upperIp, upperPort, upperSsrc);
    }

    private async Task ProcessUpperByeAsync(SIPRequest request)
    {
        var channelId = request.Header.To.ToURI.User ?? string.Empty;
        await SendResponseAsync(request, SIPResponseStatusCodesEnum.Ok);

        var streamId = "cascade_" + channelId;
        if (_sessions.TryRemove(channelId, out var session))
        {
            using var scope = _scopeFactory.CreateScope();
            var zlm = scope.ServiceProvider.GetRequiredService<ZlmService>();
            try { await zlm.StopSendRtpAsync(streamId, session.UpperSsrc); } catch { }
            try { await zlm.CloseRtpServerAsync(streamId); } catch { }
        }
        await _sip.StopPlayAsync(streamId);
        _logger.LogInformation("[Cascade] 上级停止点播，通道 {Channel}", channelId);
    }

    private async Task ForwardPtzAsync(string channelId, string ptzCmd)
    {
        string? deviceId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            deviceId = (await db.GbChannels.FirstOrDefaultAsync(c => c.ChannelId == channelId))?.DeviceId;
        }
        if (string.IsNullOrEmpty(deviceId)) return;
        await _sip.ForwardDeviceControlAsync(deviceId, channelId, ptzCmd);
    }

    // ---------- 目录推送 ----------

    private async Task PushCatalogAsync(CascadeUpperState u, string? sn = null)
    {
        var transport = _sip.Transport;
        if (transport == null || u.EndPoint == null) return;

        List<GbCatalogItem> items;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var channels = await db.GbChannels.OrderBy(c => c.ChannelId).ToListAsync();
            items = channels.Select(c => new GbCatalogItem
            {
                DeviceId = c.ChannelId,
                Name = c.Name,
                Manufacturer = c.Manufacturer,
                Model = c.Model,
                Owner = c.Owner,
                CivilCode = c.CivilCode,
                Address = c.Address,
                Parental = c.Parental,
                ParentId = c.ParentId,
                Status = c.Status,
            }).ToList();
        }

        var snVal = sn ?? Interlocked.Increment(ref _sn).ToString();
        var xml = GbXmlCodec.BuildCatalogResponse(snVal, _localId, items);

        var toUri = GbSipMessageFactory.ToUri(u.EndPoint, u.Config.PlatformId);
        var fromUri = GbSipMessageFactory.FromUri(_localId, _sip.LocalIp, _sip.SipPort);
        var req = GbSipMessageFactory.Create(SIPMethodsEnum.MESSAGE, toUri, fromUri,
            contentType: "Application/MANSCDP+xml", cseq: ++u.CSeq, body: xml);

        try
        {
            await transport.SendRequestAsync(u.EndPoint, req);
            _logger.LogInformation("[Cascade] 向 {Name} 推送目录 {Count} 条", u.Config.Name, items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Cascade] 向 {Name} 推送目录失败", u.Config.Name);
        }
    }

    // ---------- 工具 ----------

    private async Task SendResponseAsync(SIPRequest request, SIPResponseStatusCodesEnum status)
    {
        var transport = _sip.Transport;
        if (transport == null) return;
        var resp = SIPResponse.GetResponse(request, status, null);
        await transport.SendResponseAsync(resp);
    }

    private async Task SendInviteOkAsync(SIPRequest request, int port, string ssrc)
    {
        var transport = _sip.Transport;
        if (transport == null) return;
        var resp = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
        resp.Header.ContentType = "application/sdp";
        resp.Body = "v=0\r\n" +
                    $"o={_localId} 0 0 IN IP4 {_sip.MediaIp}\r\n" +
                    "s=Play\r\n" +
                    $"c=IN IP4 {_sip.MediaIp}\r\n" +
                    "t=0 0\r\n" +
                    $"m=video {port} RTP/AVP 96\r\n" +
                    "a=sendonly\r\n" +
                    $"y={ssrc}\r\n";
        await transport.SendResponseAsync(resp);
    }
}
