using System.Net;
using SIPSorcery.SIP;

namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>
/// SIP 请求统一构造工厂：归一 GbSipEngine / GbCascadeEngine 中 8 处重复的
/// MESSAGE / INVITE / BYE / INFO / REGISTER 头部拼装（Contact/UserAgent/CallId/CSeq 等）。
/// </summary>
public static class GbSipMessageFactory
{
    private const string UserAgent = "Apiext-MediaKit";

    /// <summary>目标 URI（User = 通道/设备/上级平台编码）。</summary>
    public static SIPURI ToUri(SIPEndPoint remote, string user)
        => new(SIPSchemesEnum.sip, remote) { User = user };

    /// <summary>平台侧源 URI（User = 本平台编码）。</summary>
    public static SIPURI FromUri(string localId, string localIp, int sipPort)
        => new(SIPSchemesEnum.sip, IPAddress.Parse(localIp), sipPort) { User = localId };

    /// <summary>
    /// 构造带标准头的 SIP 请求。toTag/fromTag 传 null 表示新请求
    /// （GbSipEngine 新会话 FromTag 约定 "Apiext-MediaKit"，由调用方显式传入）；callId 缺省生成新 Guid。
    /// </summary>
    public static SIPRequest Create(
        SIPMethodsEnum method, SIPURI toUri, SIPURI fromUri,
        string? toTag = null, string? fromTag = null,
        string? contentType = null, string? subject = null,
        string? callId = null, int? cseq = null, string? body = null)
    {
        var req = SIPRequest.GetRequest(method, toUri,
            new SIPToHeader(null, toUri, toTag),
            new SIPFromHeader(null, fromUri, fromTag));
        req.Header.Contact = new List<SIPContactHeader> { new(null, fromUri) };
        req.Header.UserAgent = UserAgent;
        if (contentType != null) req.Header.ContentType = contentType;
        if (subject != null) req.Header.Subject = subject;
        req.Header.CallId = callId ?? Guid.NewGuid().ToString("N");
        req.Header.CSeq = cseq ?? 1;
        if (body != null) req.Body = body;
        return req;
    }
}
