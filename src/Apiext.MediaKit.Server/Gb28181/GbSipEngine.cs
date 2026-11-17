using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Apiext.MediaKit.Server.Persistence;
using Apiext.MediaKit.Server.Services;
using Microsoft.EntityFrameworkCore;
using SIPSorcery.SIP;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>GB28181 设备内存状态（信令端点 + 心跳）。</summary>
public class GbDeviceState
{
    public string DeviceId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public SIPEndPoint RemoteEndPoint { get; set; } = null!;
    public DateTime LastKeepalive { get; set; }
}

/// <summary>一个点播/回放会话（用于后续 BYE / 控制关联）。</summary>
public class PlaySession
{
    public string ChannelId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Ssrc { get; set; } = string.Empty;
    public string CallId { get; set; } = string.Empty;
    public string FromTag { get; set; } = string.Empty;
    public string ToTag { get; set; } = string.Empty;
    public int CSeq { get; set; }

    /// <summary>ZLM 收流 stream_id（实时=通道编码，回放=SSRC）。</summary>
    public string StreamId { get; set; } = string.Empty;

    /// <summary>是否录像回放。</summary>
    public bool IsPlayback { get; set; }
}

/// <summary>
/// GB28181 SIP 信令引擎：作为 SIP 服务器（上级/平台）监听 UDP 端口，
/// 处理设备注册、心跳、目录响应，并主动发起点播 INVITE / BYE。
/// 仅负责信令；媒体面由 ZLMediaKit 的 RTP 接口承载。
/// </summary>
public class GbSipEngine : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly GbDeviceStore _store;
    private readonly GbAuthenticator _authenticator;
    private readonly ILogger<GbSipEngine> _logger;

    private SIPTransport? _transport;

    // 配置
    private int _sipPort = 28060;
    private string _sipId = string.Empty;
    private string _sipDomain = string.Empty;
    private string _realm = string.Empty;
    private string _password = string.Empty;
    private int _heartbeatTimeout = 300;
    private bool _enableAuth;
    private string _localIp = string.Empty;
    private string _mediaIp = string.Empty;

    // 内存状态
    private readonly ConcurrentDictionary<string, GbDeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _inviteTcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<List<GbCatalogItem>>> _catalogTcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<List<GbRecordItem>>> _recordTcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PlaySession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private int _sn;
    private int _cseq;
    private readonly Random _random = new();

    public GbSipEngine(
        IConfiguration config, GbDeviceStore store, GbAuthenticator authenticator, ILogger<GbSipEngine> logger)
    {
        _config = config;
        _store = store;
        _authenticator = authenticator;
        _logger = logger;
        ReadConfig();
    }

    private void ReadConfig()
    {
        var section = _config.GetSection("Gb28181");
        _sipPort = section.GetValue("SipPort", 28060);
        _sipId = section.GetValue("SipId", "34020000002000000001")!;
        _sipDomain = section.GetValue("SipDomain", "3402000000")!;
        _realm = section.GetValue("Realm", _sipDomain)!;
        if (string.IsNullOrWhiteSpace(_realm)) _realm = _sipDomain;
        // 未配置密码属于部署错误，直接启动失败（不再静默回落到弱密码）
        var password = section.GetValue<string?>("Password");
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "缺少配置 Gb28181:Password（设备统一接入密码），请在 appsettings.json 或部署覆盖层中配置。");
        }
        _password = password;
        _heartbeatTimeout = section.GetValue("HeartbeatTimeout", 300);
        _enableAuth = section.GetValue("EnableAuth", true);
        _localIp = section.GetValue("LocalIp", "")!;
        if (string.IsNullOrWhiteSpace(_localIp))
        {
            _localIp = GetLocalIPv4();
        }
        // 媒体接收地址（SDP c= 行）：设备推流目标，指向 ZLM 媒体服务器对设备可见的地址。
        _mediaIp = section.GetValue("MediaIp", "")!;
        if (string.IsNullOrWhiteSpace(_mediaIp))
        {
            _mediaIp = _localIp;
        }
    }

    public bool IsRunning => _transport != null;

    /// <summary>SIP 传输层（供级联引擎复用同一 UDP 端口收发信令）。</summary>
    public SIPTransport? Transport => _transport;

    /// <summary>外部级联对端判定器（由 GbCascadeEngine 注册；命中则本引擎跳过处理，交给级联引擎）。</summary>
    public Func<SIPEndPoint, bool>? ExternalPeerFilter;

    /// <summary>平台 SIP 参数（供前端引导展示设备端配置）。</summary>
    public int SipPort => _sipPort;
    public string SipId => _sipId;
    public string SipDomain => _sipDomain;
    public string LocalIp => _localIp;
    public string MediaIp => _mediaIp;
    public bool EnableAuth => _enableAuth;

    public IReadOnlyCollection<GbDeviceState> OnlineDevices => _devices.Values.ToList();

    public GbDeviceState? GetDevice(string deviceId)
        => _devices.TryGetValue(deviceId, out var s) ? s : null;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartTransport();
        return RunHeartbeatLoopAsync(stoppingToken);
    }

    private void StartTransport()
    {
        try
        {
            _transport = new SIPTransport();
            _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, _sipPort)));
            _transport.SIPTransportRequestReceived += OnRequestReceived;
            _transport.SIPTransportResponseReceived += OnResponseReceived;
            _logger.LogInformation("[GB28181] SIP 服务已启动，监听 UDP {Port}，平台编码 {SipId}，本机 IP {Ip}",
                _sipPort, _sipId, _localIp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GB28181] SIP 服务启动失败");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_transport != null)
        {
            _transport.SIPTransportRequestReceived -= OnRequestReceived;
            _transport.SIPTransportResponseReceived -= OnResponseReceived;
            _transport.Shutdown();
            _transport = null;
        }
        return base.StopAsync(cancellationToken);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTime.UtcNow;
            foreach (var state in _devices.Values)
            {
                if ((now - state.LastKeepalive).TotalSeconds > _heartbeatTimeout)
                {
                    if (_devices.TryRemove(state.DeviceId, out _))
                    {
                        _logger.LogInformation("[GB28181] 设备 {Id} 心跳超时，判定离线", state.DeviceId);
                        await MarkOfflineAsync(state.DeviceId);
                    }
                }
            }
        }
    }

    // ---------- 请求处理（设备主动发来） ----------

    private async Task OnRequestReceived(SIPEndPoint local, SIPEndPoint remote, SIPRequest request)
    {
        // 上级平台（级联对端）发来的请求交给 GbCascadeEngine 处理，本引擎不再当作设备请求
        if (ExternalPeerFilter?.Invoke(remote) == true)
        {
            return;
        }

        try
        {
            switch (request.Method)
            {
                case SIPMethodsEnum.REGISTER:
                    await ProcessRegisterAsync(remote, request);
                    break;
                case SIPMethodsEnum.MESSAGE:
                    await ProcessMessageAsync(remote, request);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GB28181] 处理请求异常");
        }
    }

    private async Task ProcessRegisterAsync(SIPEndPoint remote, SIPRequest request)
    {
        var deviceId = request.Header.From.FromURI.User.ToUpperInvariant();
        if (string.IsNullOrEmpty(deviceId)) return;

        var contact = request.Header.Contact?.FirstOrDefault();
        var expires = contact != null && contact.Expires > 0 ? contact.Expires : request.Header.Expires;

        if (expires <= 0)
        {
            // 注销
            if (_devices.TryRemove(deviceId, out _))
            {
                _logger.LogInformation("[GB28181] 设备 {Id} 注销", deviceId);
                await MarkOfflineAsync(deviceId);
            }
        }
        else
        {
            // 接入模式判定 + 白名单校验 + Digest 鉴权
            var (allowed, requireAuth, password, presetName) = await CheckAccessAsync(deviceId);
            if (!allowed)
            {
                _logger.LogWarning("[GB28181] 设备 {Id} 未预登记，拒绝接入（白名单模式）", deviceId);
                var forbidden = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Forbidden, null);
                await _transport!.SendResponseAsync(forbidden);
                return;
            }

            if (requireAuth && !await Authenticate(request, deviceId, password))
            {
                return;
            }

            var state = _devices.GetOrAdd(deviceId, _ => new GbDeviceState { DeviceId = deviceId });
            state.Host = remote.Address.ToString();
            state.Port = remote.Port;
            state.RemoteEndPoint = remote;
            state.LastKeepalive = DateTime.UtcNow;

            await PersistDeviceAsync(deviceId, state.Host, state.Port, presetName);
        }

        var ok = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
        ok.Header.Expires = expires > 0 ? expires : _heartbeatTimeout;
        await _transport!.SendResponseAsync(ok);
    }

    /// <summary>接入判定（委托 GbAuthenticator：auto 注册即接入 / whitelist 预登记白名单）。</summary>
    private Task<(bool Allowed, bool RequireAuth, string Password, string? PresetName)> CheckAccessAsync(string deviceId)
        => _authenticator.CheckAccessAsync(deviceId);

    /// <summary>Digest 鉴权：无 Authorization 回 401 挑战；有则按给定密码校验 response（校验委托 GbAuthenticator）。</summary>
    private async Task<bool> Authenticate(SIPRequest request, string deviceId, string password)
    {
        if (request.Header.AuthenticationHeaders == null || request.Header.AuthenticationHeaders.Count == 0)
        {
            var nonce = GbDigest.GenerateNonce();
            var authHeader = new SIPAuthenticationHeader(SIPAuthorisationHeadersEnum.WWWAuthenticate, _realm, nonce);
            authHeader.SIPDigest.DigestAlgorithm = DigestAlgorithmsEnum.MD5;
            var unauthorized = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Unauthorised, null);
            unauthorized.Header.AuthenticationHeaders.Add(authHeader);
            await _transport!.SendResponseAsync(unauthorized);
            _logger.LogDebug("[GB28181] 设备 {Id} 首次注册，已回 401 挑战", deviceId);
            return false;
        }

        var ok = GbAuthenticator.ValidateDigest(request, password);
        if (!ok)
        {
            _logger.LogWarning("[GB28181] 设备 {Id} 鉴权失败", deviceId);
        }
        return ok;
    }

    private async Task ProcessMessageAsync(SIPEndPoint remote, SIPRequest request)
    {
        var cmdType = GbXmlCodec.ExtractXmlValue(request.Body, "CmdType")?.ToUpperInvariant();
        if (string.IsNullOrEmpty(cmdType)) return;

        switch (cmdType)
        {
            case "KEEPALIVE":
            {
                var deviceId = GbXmlCodec.ExtractXmlValue(request.Body, "DeviceID")?.ToUpperInvariant() ?? string.Empty;
                if (!string.IsNullOrEmpty(deviceId) && _devices.TryGetValue(deviceId, out var state))
                {
                    state.Host = remote.Address.ToString();
                    state.Port = remote.Port;
                    state.RemoteEndPoint = remote;
                    state.LastKeepalive = DateTime.UtcNow;
                    await PersistDeviceAsync(deviceId, state.Host, state.Port, null);
                }
                else if (!string.IsNullOrEmpty(deviceId) && await _store.ExistsAsync(deviceId))
                {
                    // 平台重启后内存注册状态丢失，而设备要等满注册有效期才会重发 REGISTER；
                    // 心跳本身就是在线证据，这里按库内已有设备就地恢复（白名单模式仍校验接入）。
                    var (allowed, _, _, presetName) = await CheckAccessAsync(deviceId);
                    if (!allowed)
                    {
                        _logger.LogWarning("[GB28181] 设备 {Id} 心跳恢复被拒（白名单模式未预登记）", deviceId);
                        await SendOkAsync(request);
                        break;
                    }

                    var restored = _devices.GetOrAdd(deviceId, _ => new GbDeviceState { DeviceId = deviceId });
                    restored.Host = remote.Address.ToString();
                    restored.Port = remote.Port;
                    restored.RemoteEndPoint = remote;
                    restored.LastKeepalive = DateTime.UtcNow;
                    await PersistDeviceAsync(deviceId, restored.Host, restored.Port, presetName);
                    _logger.LogInformation("[GB28181] 设备 {Id} 由心跳恢复在线（平台重启后），来源 {Host}:{Port}",
                        deviceId, restored.Host, restored.Port);
                }
                await SendOkAsync(request);
                break;
            }
            case "CATALOG":
            {
                await SendOkAsync(request);
                var sn = GbXmlCodec.ExtractXmlValue(request.Body, "SN") ?? string.Empty;
                var items = GbXmlCodec.ParseCatalogResponse(request.Body);
                var deviceId = GbXmlCodec.ExtractXmlValue(request.Body, "DeviceID")?.ToUpperInvariant() ?? string.Empty;
                if (!string.IsNullOrEmpty(deviceId) && items.Count > 0)
                {
                    await PersistChannelsAsync(deviceId, items);
                }
                _logger.LogInformation("[GB28181] 收到目录响应 device={Device}，通道 {Count} 条", deviceId, items.Count);
                if (!string.IsNullOrEmpty(sn) && _catalogTcs.TryRemove(sn, out var tcs))
                {
                    tcs.TrySetResult(items);
                }
                break;
            }
            case "RECORDINFO":
            {
                await SendOkAsync(request);
                var sn = GbXmlCodec.ExtractXmlValue(request.Body, "SN") ?? string.Empty;
                var records = GbRecordCodec.ParseRecordListXml(request.Body);
                if (!string.IsNullOrEmpty(sn) && _recordTcs.TryRemove(sn, out var tcs))
                {
                    tcs.TrySetResult(records);
                }
                _logger.LogInformation("[GB28181] 收到录像列表 {Count} 条", records.Count);
                break;
            }
            case "MEDIASTATUS":
                // 设备主动通知回放结束，忽略（平台侧按 BYE 清理）
                await SendOkAsync(request);
                break;
            case "ALARM":
            {
                await SendOkAsync(request);
                var alarm = GbAlarmCodec.ParseAlarmNotify(request.Body);
                if (alarm != null)
                {
                    await PersistAlarmAsync(alarm);
                    _logger.LogInformation("[GB28181] 收到报警 {Desc}（{Dev}）", alarm.AlarmDescription, alarm.DeviceId);
                }
                break;
            }
            default:
                // 其它消息（DeviceInfo/DeviceStatus/Alarm 等）仅确认
                await SendOkAsync(request);
                break;
        }
    }

    // ---------- 响应处理（点播 INVITE / BYE 的应答） ----------

    private async Task OnResponseReceived(SIPEndPoint local, SIPEndPoint remote, SIPResponse response)
    {
        try
        {
            if (response.Status != SIPResponseStatusCodesEnum.Ok)
            {
                // 点播被设备拒绝时把状态码打出来，否则前端只能看到笼统的“点播超时”
                // 100 Trying / 180 Ringing 属正常临时应答，不记警告
                if (response.Header.CSeqMethod == SIPMethodsEnum.INVITE && (int)response.Status >= 300)
                {
                    _logger.LogWarning("[GB28181] 点播收到非 200 应答 {Status} CallId={CallId}",
                        response.Status, response.Header.CallId);
                }
                return;
            }

            var method = response.Header.CSeqMethod;
            var callId = response.Header.CallId;

            if (method == SIPMethodsEnum.INVITE && _inviteTcs.TryRemove(callId, out var tcs))
            {
                // 记录设备侧 ToTag / CSeq 供 BYE 使用
                var session = _sessions.Values.FirstOrDefault(s => s.CallId == callId);
                if (session != null)
                {
                    session.ToTag = response.Header.To.ToTag;
                    session.CSeq = response.Header.CSeq;
                }
                await SendAckAsync(response);
                tcs.TrySetResult(true);
                _logger.LogInformation("[GB28181] 点播 INVITE 成功 CallId={CallId}", callId);
            }
            else if (method == SIPMethodsEnum.BYE)
            {
                _logger.LogInformation("[GB28181] BYE 应答 CallId={CallId}", callId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GB28181] 处理响应异常");
        }
    }

    private async Task SendOkAsync(SIPRequest request)
    {
        var ok = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
        await _transport!.SendResponseAsync(ok);
    }

    private async Task SendAckAsync(SIPResponse response)
    {
        var from = response.Header.From;
        var to = response.Header.To;
        var ack = SIPRequest.GetRequest(SIPMethodsEnum.ACK, response.Header.To.ToURI,
            new SIPToHeader(to.ToName, to.ToURI, to.ToTag),
            new SIPFromHeader("", from.FromURI, from.FromTag));
        ack.Header.Contact = new List<SIPContactHeader> { new SIPContactHeader(from.FromName, from.FromURI) };
        ack.Header.CallId = response.Header.CallId;
        ack.Header.CSeq = response.Header.CSeq;
        await _transport!.SendRequestAsync(response.RemoteSIPEndPoint, ack);
    }

    // ---------- 对外方法（供 Gb28181Service 调用） ----------

    /// <summary>查询设备目录：发 MESSAGE，等待设备回目录响应。</summary>
    public async Task<(bool Ok, string? Error, List<GbCatalogItem>? Items)> QueryCatalogAsync(string deviceId)
    {
        if (_transport == null) return (false, "SIP 服务未启动", null);
        if (!_devices.TryGetValue(deviceId, out var state)) return (false, "设备离线", null);

        var sn = Interlocked.Increment(ref _sn).ToString();
        var xml = GbXmlCodec.BuildCatalogQuery(sn, deviceId);

        // 先登记等待槽再发送：设备可能回得很快，晚登记会丢响应并误判超时
        var tcs = new TaskCompletionSource<List<GbCatalogItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _catalogTcs[sn] = tcs;

        try
        {
            await SendMessageAsync(state, deviceId, xml, sn);
        }
        catch (Exception ex)
        {
            _catalogTcs.TryRemove(sn, out _);
            return (false, $"发送目录查询失败：{ex.Message}", null);
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        if (done != tcs.Task)
        {
            _catalogTcs.TryRemove(sn, out _);
            _logger.LogWarning("[GB28181] 目录查询超时 device={Device} sn={Sn}（设备未回 Catalog 响应）", deviceId, sn);
            return (false, "目录查询超时", null);
        }

        var items = await tcs.Task;
        return (true, null, items);
    }

    /// <summary>查询通道录像列表：发 MESSAGE，等待设备回 RecordInfo 响应。</summary>
    public async Task<(bool Ok, string? Error, List<GbRecordItem>? Records)> RecordQueryAsync(
        string deviceId, string channelId, DateTime start, DateTime end)
    {
        if (_transport == null) return (false, "SIP 服务未启动", null);
        if (!_devices.TryGetValue(deviceId, out var state)) return (false, "设备离线", null);

        var sn = Interlocked.Increment(ref _sn).ToString();
        var xml = GbRecordCodec.BuildRecordQueryXml(sn, channelId, start, end);
        var subject = $"{channelId}:{0},{_sipId}:{_random.Next(100, ushort.MaxValue)}";

        // 先登记等待槽再发送，避免设备秒回时丢响应
        var tcs = new TaskCompletionSource<List<GbRecordItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _recordTcs[sn] = tcs;

        try
        {
            await SendChannelMessageAsync(state, channelId, xml, subject);
        }
        catch (Exception ex)
        {
            _recordTcs.TryRemove(sn, out _);
            return (false, $"发送录像查询失败：{ex.Message}", null);
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (done != tcs.Task)
        {
            _recordTcs.TryRemove(sn, out _);
            return (false, "录像查询超时", null);
        }

        return (true, null, await tcs.Task);
    }

    /// <summary>向通道发 MESSAGE（To = 通道编码）。</summary>
    private async Task SendChannelMessageAsync(GbDeviceState state, string channelId, string xml, string? subject = null)
    {
        var toUri = GbSipMessageFactory.ToUri(state.RemoteEndPoint, channelId);
        var fromUri = GbSipMessageFactory.FromUri(_sipId, _localIp, _sipPort);
        var req = GbSipMessageFactory.Create(SIPMethodsEnum.MESSAGE, toUri, fromUri,
            fromTag: "Apiext-MediaKit", contentType: "Application/MANSCDP+xml", subject: subject,
            cseq: Interlocked.Increment(ref _cseq), body: xml);
        await _transport!.SendRequestAsync(state.RemoteEndPoint, req);
    }

    /// <summary>发起点播/回放 INVITE：等设备 200 OK 后自动回 ACK，设备开始推 RTP 到 ZLM。</summary>
    public async Task<(bool Ok, string? Error)> InviteAsync(
        string deviceId, string channelId, string streamId, int rtpPort, string ssrc,
        bool isPlayback = false, DateTime? playStart = null, DateTime? playEnd = null)
    {
        if (_transport == null) return (false, "SIP 服务未启动");
        if (!_devices.TryGetValue(deviceId, out var state)) return (false, "设备离线");

        var toUri = GbSipMessageFactory.ToUri(state.RemoteEndPoint, channelId);
        var fromUri = GbSipMessageFactory.FromUri(_sipId, _localIp, _sipPort);
        var callId = Guid.NewGuid().ToString("N");
        var cseq = Interlocked.Increment(ref _cseq);
        // 实时：Subject = 通道ID:SSRC,平台ID:随机；回放：通道ID:0,平台ID:随机
        var subject = isPlayback
            ? $"{channelId}:{0},{_sipId}:{_random.Next(100, ushort.MaxValue)}"
            : $"{channelId}:{ssrc},{_sipId}:{_random.Next(100, ushort.MaxValue)}";

        var req = GbSipMessageFactory.Create(SIPMethodsEnum.INVITE, toUri, fromUri,
            fromTag: "Apiext-MediaKit", contentType: "application/sdp", subject: subject,
            callId: callId, cseq: cseq,
            body: isPlayback
                ? GbSdpCodec.BuildPlaybackSdp(_sipId, _mediaIp, rtpPort, ssrc, playStart ?? DateTime.Now, playEnd ?? DateTime.Now)
                : GbSdpCodec.BuildInviteSdp(_sipId, _mediaIp, rtpPort, ssrc));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inviteTcs[callId] = tcs;
        _sessions[streamId] = new PlaySession
        {
            ChannelId = channelId,
            DeviceId = deviceId,
            Ssrc = ssrc,
            StreamId = streamId,
            IsPlayback = isPlayback,
            CallId = callId,
            FromTag = "Apiext-MediaKit",
            CSeq = cseq,
        };

        try
        {
            await _transport.SendRequestAsync(state.RemoteEndPoint, req);
        }
        catch (Exception ex)
        {
            _inviteTcs.TryRemove(callId, out _);
            _sessions.TryRemove(streamId, out _);
            return (false, $"发送点播失败：{ex.Message}");
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (done != tcs.Task)
        {
            _inviteTcs.TryRemove(callId, out _);
            _sessions.TryRemove(streamId, out _);
            _logger.LogWarning("[GB28181] 点播超时 device={Device} channel={Channel} 目标={Target}（设备未回 200 OK）",
                deviceId, channelId, state.RemoteEndPoint);
            return (false, "点播超时");
        }

        return (true, null);
    }

    /// <summary>停止点播/回放：发 BYE，并清理会话。</summary>
    public async Task StopPlayAsync(string streamId)
    {
        if (_transport == null || !_sessions.TryRemove(streamId, out var session)) return;
        if (!_devices.TryGetValue(session.DeviceId, out var state)) return;

        try
        {
            var toUri = GbSipMessageFactory.ToUri(state.RemoteEndPoint, session.ChannelId);
            var fromUri = GbSipMessageFactory.FromUri(_sipId, _localIp, _sipPort);
            var bye = GbSipMessageFactory.Create(SIPMethodsEnum.BYE, toUri, fromUri,
                toTag: session.ToTag, fromTag: session.FromTag,
                callId: session.CallId, cseq: session.CSeq + 1);
            await _transport.SendRequestAsync(state.RemoteEndPoint, bye);
            _logger.LogInformation("[GB28181] 已发送 BYE 停止，通道 {Channel}，流 {Stream}", session.ChannelId, streamId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GB28181] 发送 BYE 失败，流 {Stream}", streamId);
        }
    }

    /// <summary>回放控制（暂停/继续/拖动）：发 INFO，body 为 PAUSE 或 PLAY MANSRTSP。</summary>
    public async Task PlaybackControlAsync(string streamId, long rangeTime, bool pause = false)
    {
        if (_transport == null || !_sessions.TryGetValue(streamId, out var session)) return;
        if (!_devices.TryGetValue(session.DeviceId, out var state)) return;

        try
        {
            var toUri = GbSipMessageFactory.ToUri(state.RemoteEndPoint, session.ChannelId);
            var fromUri = GbSipMessageFactory.FromUri(_sipId, _localIp, _sipPort);

            var cseq = ++session.CSeq;
            var body = pause
                ? $"PAUSE MANSRTSP/1.0\r\nCSeq: {cseq}\r\n"
                : $"PLAY MANSRTSP/1.0\r\nCSeq: {cseq}\r\nRange: npt={rangeTime}-\r\n";

            var info = GbSipMessageFactory.Create(SIPMethodsEnum.INFO, toUri, fromUri,
                toTag: session.ToTag, fromTag: session.FromTag,
                contentType: "Application/SDP",
                callId: session.CallId, cseq: cseq, body: body);
            await _transport.SendRequestAsync(state.RemoteEndPoint, info);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GB28181] 回放控制失败，流 {Stream}", streamId);
        }
    }

    private async Task SendMessageAsync(GbDeviceState state, string deviceId, string xml, string sn)
    {
        var toUri = GbSipMessageFactory.ToUri(state.RemoteEndPoint, deviceId);
        var fromUri = GbSipMessageFactory.FromUri(_sipId, _localIp, _sipPort);
        var req = GbSipMessageFactory.Create(SIPMethodsEnum.MESSAGE, toUri, fromUri,
            fromTag: "Apiext-MediaKit", contentType: "Application/MANSCDP+xml",
            subject: $"{_sipId}:{0},{deviceId}:{_random.Next(100, ushort.MaxValue)}",
            cseq: Interlocked.Increment(ref _cseq), body: xml);
        await _transport!.SendRequestAsync(state.RemoteEndPoint, req);
    }

    /// <summary>云台控制：向通道发 DeviceControl MESSAGE。</summary>
    public async Task PtzControlAsync(string deviceId, string channelId, PtzCommand command, int param)
    {
        if (_transport == null || !_devices.TryGetValue(deviceId, out var state)) return;
        await SendDeviceControlAsync(state, channelId, GbPtz.Encode(command, param));
    }

    /// <summary>转发上级平台下发的原始 PTZ 指令（十六进制串）到设备（平台级联）。</summary>
    public async Task ForwardDeviceControlAsync(string deviceId, string channelId, string ptzCmd)
    {
        if (_transport == null || !_devices.TryGetValue(deviceId, out var state)) return;
        await SendDeviceControlAsync(state, channelId, ptzCmd);
    }

    private async Task SendDeviceControlAsync(GbDeviceState state, string channelId, string ptzCmd)
    {
        var sn = Interlocked.Increment(ref _sn).ToString();
        var xml = GbPtz.BuildControlXml(sn, channelId, ptzCmd);

        var toUri = GbSipMessageFactory.ToUri(state.RemoteEndPoint, channelId);
        var fromUri = GbSipMessageFactory.FromUri(_sipId, _localIp, _sipPort);
        var req = GbSipMessageFactory.Create(SIPMethodsEnum.MESSAGE, toUri, fromUri,
            fromTag: "Apiext-MediaKit", contentType: "Application/MANSCDP+xml",
            cseq: Interlocked.Increment(ref _cseq), body: xml);
        await _transport.SendRequestAsync(state.RemoteEndPoint, req);
    }

    /// <summary>订阅通道报警：向通道发 Alarm 订阅 MESSAGE。</summary>
    public async Task SubscribeAlarmAsync(string deviceId, string channelId)
    {
        if (_transport == null || !_devices.TryGetValue(deviceId, out var state)) return;

        var sn = Interlocked.Increment(ref _sn).ToString();
        var xml = GbAlarmCodec.BuildSubscribeXml(sn, channelId);
        var subject = $"{channelId}:{0},{_sipId}:{_random.Next(100, ushort.MaxValue)}";
        await SendChannelMessageAsync(state, channelId, xml, subject);
    }

    // ---------- 持久化（委托 GbDeviceStore，信令引擎不再直连 EF Core） ----------

    private Task PersistDeviceAsync(string deviceId, string host, int port, string? name)
        => _store.UpsertOnlineAsync(deviceId, host, port, name);

    private Task MarkOfflineAsync(string deviceId)
        => _store.MarkOfflineAsync(deviceId);

    private Task PersistChannelsAsync(string deviceId, List<GbCatalogItem> items)
        => _store.SaveChannelsAsync(deviceId, items);

    private Task PersistAlarmAsync(GbAlarm alarm)
        => _store.SaveAlarmAsync(alarm);

    // ---------- 工具 ----------

    private static string GetLocalIPv4() => Apiext.MediaKit.Server.Services.NetworkInfo.DetectOutboundIPv4();
}
