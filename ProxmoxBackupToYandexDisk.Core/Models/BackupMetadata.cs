namespace ProxmoxBackupToYandexDisk.Core.Models;

/// <summary>
/// Metadata for a backup file or chunk
/// </summary>
public sealed record BackupMetadata
{
    /// <summary>
    /// Metadata version for backward compatibility.
    /// 1 = legacy (file-based upload), 2 = streaming upload
    /// </summary>
    public int MetadataVersion { get; init; } = 1;

    public required string OriginalFileName { get; init; }
    public required long OriginalFileSize { get; init; }
    public required string OriginalFileHash { get; init; }
    public required int TotalChunks { get; init; }
    public required long ChunkSize { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string ProxmoxVersion { get; init; }
    public required string VmType { get; init; }
    public required int VmId { get; init; }
    public required string VmName { get; init; }
    public required string NodeName { get; init; }
    public List<ChunkInfo> Chunks { get; init; } = [];
}

public sealed record ChunkInfo
{
    public required int Index { get; init; }
    public required string FileName { get; init; }
    public required long Size { get; init; }
    public required string Hash { get; init; }
    public required long Offset { get; init; }
}
