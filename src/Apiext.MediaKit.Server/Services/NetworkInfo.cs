using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Apiext.MediaKit.Server.Services;

/// <summary>
/// 本机网络地址探测。用于「配置留空也能开箱即用」：
/// SIP 本地地址、SDP 媒体地址、对外播放地址在未显式配置时都回落到这里的探测结果。
/// </summary>
public static class NetworkInfo
{
    /// <summary>
    /// 探测本机对外 IPv4：优先取**默认路由**所在网卡（UDP connect 不会真的发包），
    /// 避免多网卡（VMware/Hyper-V/Docker 虚拟网卡）时选错地址；失败再退化为首个 Up 网卡。
    /// </summary>
    public static string DetectOutboundIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("223.5.5.5", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
            {
                return ep.Address.ToString();
            }
        }
        catch
        {
            // 无默认路由（纯内网/离线环境）时走回退
        }

        return FirstUpIPv4();
    }

    /// <summary>是否运行在容器内（自动探测到的可能是容器地址，需用 PublicHost 覆盖）。</summary>
    public static bool IsInContainer()
        => File.Exists("/.dockerenv")
           || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    private static string FirstUpIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }

        return "127.0.0.1";
    }
}
