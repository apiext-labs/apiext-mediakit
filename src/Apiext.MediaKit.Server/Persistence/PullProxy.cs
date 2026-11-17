namespace Apiext.MediaKit.Server.Persistence;

/// <summary>拉流代理持久化记录</summary>
public class PullProxy
{
    public int Id { get; set; }
    public string Vhost { get; set; } = "__defaultVhost__";
    public string App { get; set; } = string.Empty;
    public string Stream { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? AudioType { get; set; }

    /// <summary>取流模式：native（ZLM 内核拉流）| ffmpeg（FFmpeg 拉流后推流到 ZLM）</summary>
    public string PullMode { get; set; } = "native";

    /// <summary>输出编码：copy（直通）| h264 | h265（仅 ffmpeg 模式生效）</summary>
    public string OutputCodec { get; set; } = "copy";

    /// <summary>FFmpeg 模式下 addFFmpegSource 返回的 key（删除用）</summary>
    public string FfmpegKey { get; set; } = string.Empty;

    /// <summary>按需拉流：无人观看时自动断流，有播放请求时再拉流</summary>
    public bool EnableOnDemand { get; set; }

    /// <summary>是否录制该流（本地 MP4 录像）</summary>
    public bool EnableRecord { get; set; }

    public DateTime CreatedAt { get; set; }
}
