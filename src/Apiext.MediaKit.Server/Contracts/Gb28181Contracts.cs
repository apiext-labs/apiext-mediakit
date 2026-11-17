namespace Apiext.MediaKit.Server.Contracts;

/// <summary>国标云台控制请求体。</summary>
public sealed class GbPtzRequest
{
    public string Command { get; set; } = "Stop";
    public int Param { get; set; } = 5;
}

/// <summary>国标回放控制请求体。</summary>
public sealed class GbPlaybackControlRequest
{
    public long RangeTime { get; set; }
    public bool Pause { get; set; }
}
