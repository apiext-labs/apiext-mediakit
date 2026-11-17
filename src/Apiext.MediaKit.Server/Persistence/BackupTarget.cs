namespace Apiext.MediaKit.Server.Persistence;

/// <summary>录像备份目标：把本地录像目录同步到 SMB/NFS/S3 等挂载目录。</summary>
public class BackupTarget
{
    public int Id { get; set; }

    /// <summary>目标名称（展示用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>备份类型：smb / nfs / s3（用于分类展示，实际挂载由宿主机/容器完成）。</summary>
    public string Type { get; set; } = "nfs";

    /// <summary>本地录像源目录；留空则使用 appsettings Recording:SourcePath。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>备份目标挂载目录（SMB/NFS 挂载点）。</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>S3 服务端点（如 http://minio:9000 / https://s3.region.amazonaws.com），仅 s3 类型使用。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>S3 区域（如 us-east-1），仅 s3 类型使用。</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>S3 AccessKey。</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>S3 SecretKey。</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>S3 Bucket 名称。</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>S3 对象键前缀（目录，可空）。</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>是否启用自动备份。</summary>
    public bool Enabled { get; set; }

    /// <summary>最近一次备份时间。</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>最近一次备份结果描述。</summary>
    public string LastResult { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
