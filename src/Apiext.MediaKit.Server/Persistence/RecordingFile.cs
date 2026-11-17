namespace Apiext.MediaKit.Server.Persistence;

/// <summary>
/// 本地 MP4 录像索引（由 ZLM 的 on_record_mp4 hook 写入，对账服务补齐/校正）。
/// </summary>
public class RecordingFile
{
    /// <summary>索引来源：hook 回调。</summary>
    public const string SourceHook = "hook";

    /// <summary>索引来源：对账扫描磁盘。</summary>
    public const string SourceReconcile = "reconcile";

    public int Id { get; set; }

    public string Vhost { get; set; } = "__defaultVhost__";

    /// <summary>应用名（录制时为流的 app）。</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>流 ID。</summary>
    public string Stream { get; set; } = string.Empty;

    /// <summary>录像文件名（如 12-30-00.mp4），与 App/Stream 组合唯一。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>相对录制根目录的目录（如 record/live/cam1/2026-11-12）。</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>ZLM 侧的绝对文件路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>ZLM 提供的 HTTP 下载地址（未带播放令牌）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long FileSize { get; set; }

    /// <summary>录像时长（秒）。</summary>
    public long DurationSec { get; set; }

    /// <summary>切片开始时间（UTC）。</summary>
    public DateTime StartTimeUtc { get; set; }

    /// <summary>索引来源：hook / reconcile。</summary>
    public string Source { get; set; } = SourceHook;

    /// <summary>最后一次确认文件存在的时间（UTC）。</summary>
    public DateTime? LastSeenAtUtc { get; set; }

    /// <summary>判定文件已丢失的时间（UTC）；非空表示索引存在但文件不在。</summary>
    public DateTime? MissingAtUtc { get; set; }

    /// <summary>连续对账未命中次数（达到阈值才标记丢失，避免网络盘抖动误判）。</summary>
    public int ReconcileMissCount { get; set; }

    public DateTime CreatedAt { get; set; }
}
