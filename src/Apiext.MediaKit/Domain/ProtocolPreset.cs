using Volo.Abp.Domain.Entities.Auditing;

namespace Apiext.MediaKit.Domain;

public sealed class ProtocolPreset : FullAuditedAggregateRoot<Guid>
{
    public string Name { get; private set; } = string.Empty;
    public bool EnableAudio { get; private set; } = true;
    public bool AddMuteAudio { get; private set; }
    public bool EnableHls { get; private set; } = true;
    public bool EnableMp4 { get; private set; }
    public bool EnableRtsp { get; private set; } = true;
    public bool EnableRtmp { get; private set; } = true;
    public bool EnableTs { get; private set; }
    public bool EnableFmp4 { get; private set; }
    public bool AutoClose { get; private set; }
    public int Mp4MaxSeconds { get; private set; }
    public string Mp4SavePath { get; private set; } = string.Empty;
    public string HlsSavePath { get; private set; } = string.Empty;

    private ProtocolPreset() { }

    public ProtocolPreset(Guid id, string name) : base(id) => Name = name.Trim();

    public void Update(string name, bool enableAudio, bool addMuteAudio, bool enableHls, bool enableMp4,
        bool enableRtsp, bool enableRtmp, bool enableTs, bool enableFmp4, bool autoClose,
        int mp4MaxSeconds, string? mp4SavePath, string? hlsSavePath)
    {
        Name = name.Trim();
        EnableAudio = enableAudio;
        AddMuteAudio = addMuteAudio;
        EnableHls = enableHls;
        EnableMp4 = enableMp4;
        EnableRtsp = enableRtsp;
        EnableRtmp = enableRtmp;
        EnableTs = enableTs;
        EnableFmp4 = enableFmp4;
        AutoClose = autoClose;
        Mp4MaxSeconds = Math.Max(0, mp4MaxSeconds);
        Mp4SavePath = mp4SavePath?.Trim() ?? string.Empty;
        HlsSavePath = hlsSavePath?.Trim() ?? string.Empty;
    }
}
