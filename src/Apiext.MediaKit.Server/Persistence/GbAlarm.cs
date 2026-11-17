namespace Apiext.MediaKit.Server.Persistence;

/// <summary>GB28181 设备上报的报警记录。</summary>
public class GbAlarm
{
    public int Id { get; set; }

    /// <summary>报警源（通道）国标编码</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>报警优先级（1-4）</summary>
    public string AlarmPriority { get; set; } = string.Empty;

    /// <summary>报警方式（1 电话/2 设备/3 短信/4 GPS/5 视频/6 故障/7 其他）</summary>
    public string AlarmMethod { get; set; } = string.Empty;

    public DateTime AlarmTime { get; set; }
    public string AlarmDescription { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
