namespace Apiext.MediaKit.Server.Gb28181;

/// <summary>云台控制命令。</summary>
public enum PtzCommand
{
    Stop = 0,
    Up,
    Down,
    Left,
    Right,
    UpLeft,
    UpRight,
    DownLeft,
    DownRight,
    ZoomIn,
    ZoomOut,
    FocusIn,
    FocusOut,
    IrisIn,
    IrisOut,
    SetPreset,
    GotoPreset,
    RemovePreset,
    TimeSync,
}

/// <summary>GB28181 云台控制：PTZ 命令编码 + DeviceControl XML。</summary>
public static class GbPtz
{
    /// <summary>
    /// 构造 DeviceControl 控制消息体（MESSAGE 的 body）。
    /// </summary>
    public static string BuildControlXml(string sn, string channelId, string ptzCmd)
    {
        return "<?xml version=\"1.0\"?>" +
               "<Control>" +
               "<CmdType>DeviceControl</CmdType>" +
               $"<SN>{sn}</SN>" +
               $"<DeviceID>{channelId}</DeviceID>" +
               $"<PTZCmd>{ptzCmd}</PTZCmd>" +
               "</Control>";
    }

    /// <summary>
    /// 将云台命令编码为 GB28181 PTZ 指令十六进制串。
    /// 协议：A5 0F 01 + 命令字节 + 水平速度 + 垂直速度 + 变倍/聚焦速度 + 校验和。
    /// </summary>
    /// <param name="command">控制命令</param>
    /// <param name="param">速度（1-255）；对预置位命令为预置位号</param>
    public static string Encode(PtzCommand command, int param)
    {
        var speed = Math.Clamp(param, 0, 255);
        var list = new List<int> { 0xA5, 0x0F, 0x01 };

        switch (command)
        {
            case PtzCommand.Stop:
                list.AddRange(new[] { 0x00, 0x00, 0x00, 0x00 });
                break;
            case PtzCommand.Up:
                list.AddRange(new[] { 0x08, speed, speed, 0x00 });
                break;
            case PtzCommand.Down:
                list.AddRange(new[] { 0x04, speed, speed, 0x00 });
                break;
            case PtzCommand.Left:
                list.AddRange(new[] { 0x02, speed, speed, 0x00 });
                break;
            case PtzCommand.Right:
                list.AddRange(new[] { 0x01, speed, speed, 0x00 });
                break;
            case PtzCommand.UpRight:
                list.AddRange(new[] { 0x09, speed, speed, 0x00 });
                break;
            case PtzCommand.DownRight:
                list.AddRange(new[] { 0x05, speed, speed, 0x00 });
                break;
            case PtzCommand.UpLeft:
                list.AddRange(new[] { 0x0A, speed, speed, 0x00 });
                break;
            case PtzCommand.DownLeft:
                list.AddRange(new[] { 0x06, speed, speed, 0x00 });
                break;
            case PtzCommand.ZoomIn:
                list.AddRange(new[] { 0x10, 0x00, 0x00, speed << 4 });
                break;
            case PtzCommand.ZoomOut:
                list.AddRange(new[] { 0x20, 0x00, 0x00, speed << 4 });
                break;
            case PtzCommand.FocusIn:
                list.AddRange(new[] { 0x42, speed, 0x00, 0x00 });
                break;
            case PtzCommand.FocusOut:
                list.AddRange(new[] { 0x41, speed, 0x00, 0x00 });
                break;
            case PtzCommand.IrisIn:
                list.AddRange(new[] { 0x44, 0x00, speed, 0x00 });
                break;
            case PtzCommand.IrisOut:
                list.AddRange(new[] { 0x48, 0x00, speed, 0x00 });
                break;
            case PtzCommand.SetPreset:
                list.AddRange(new[] { 0x81, 0x00, speed, 0x00 });
                break;
            case PtzCommand.GotoPreset:
                list.AddRange(new[] { 0x82, 0x00, speed, 0x80 });
                break;
            case PtzCommand.RemovePreset:
                list.AddRange(new[] { 0x83, 0x00, speed, 0x00 });
                break;
            case PtzCommand.TimeSync:
            {
                var now = DateTime.Now;
                list.AddRange(new[] { 0x90, 0x01, 0x02 });
                list.Add(ToBcd(now.Second));
                list.Add(ToBcd(now.Minute));
                list.Add(ToBcd(now.Hour));
                list.Add(ToBcd(now.Day));
                list.Add(ToBcd(now.Month));
                list.Add(ToBcd(now.Year % 100));
                list.Add((int)now.DayOfWeek);
                list.Add(0x00);
                break;
            }
        }

        var check = list.Sum() % 256;
        list.Add(check);

        return string.Concat(list.Select(b => b.ToString("X2")));
    }

    private static int ToBcd(int value)
        => ((value / 10) << 4) | (value % 10);
}
