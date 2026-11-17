namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>GB28181 SSRC 生成（10 位：首位 0=实时点播、1=回放，后 9 位随机）。</summary>
public static class GbSsrc
{
    /// <summary>实时点播 SSRC（首位 0）。</summary>
    public static string NewLive()
        => "0" + Random.Shared.Next(0, 1_000_000_000).ToString().PadLeft(9, '0');

    /// <summary>回放 SSRC（首位 1）。</summary>
    public static string NewPlayback()
        => "1" + Random.Shared.Next(0, 1_000_000_000).ToString().PadLeft(9, '0');
}
