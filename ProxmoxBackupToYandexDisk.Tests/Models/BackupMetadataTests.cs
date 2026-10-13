using System.Text.Json;
using ProxmoxBackupToYandexDisk.Core.Models;

namespace ProxmoxBackupToYandexDisk.Tests.Models;

public class BackupMetadataTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void MetadataVersion_DefaultsToOne()
    {
        // Arrange & Act
        var metadata = new BackupMetadata
        {
            OriginalFileName = "test.vma.zst",
            OriginalFileSize = 1000,
            OriginalFileHash = "abc123",
            TotalChunks = 1,
            ChunkSize = 49L * 1024 * 1024 * 1024,
            CreatedAt = DateTime.UtcNow,
            ProxmoxVersion = "8.0",
            VmType = "qemu",
            VmId = 100,
            VmName = "test-vm",
            NodeName = "pve"
        };

        // Assert
        Assert.Equal(1, metadata.MetadataVersion);
    }

    [Fact]
    public void MetadataVersion_CanBeSetToTwo()
    {
        // Arrange & Act
        var metadata = new BackupMetadata
        {
            MetadataVersion = 2,
            OriginalFileName = "test.vma.zst",
            OriginalFileSize = 1000,
            OriginalFileHash = "abc123",
            TotalChunks = 1,
            ChunkSize = 49L * 1024 * 1024 * 1024,
            CreatedAt = DateTime.UtcNow,
            ProxmoxVersion = "8.0",
            VmType = "qemu",
            VmId = 100,
            VmName = "test-vm",
            NodeName = "pve"
        };

        // Assert
        Assert.Equal(2, metadata.MetadataVersion);
    }

    [Fact]
    public void Deserialization_DefaultsMetadataVersionToOne_WhenNotPresent()
    {
        // Arrange - JSON without metadataVersion field (legacy backup)
        var json = """
        {
            "originalFileName": "test.vma.zst",
            "originalFileSize": 1000,
            "originalFileHash": "abc123",
            "totalChunks": 1,
            "chunkSize": 52613349376,
            "createdAt": "2024-01-15T12:00:00Z",
            "proxmoxVersion": "8.0",
            "vmType": "qemu",
            "vmId": 100,
            "vmName": "test-vm",
            "nodeName": "pve",
            "chunks": []
        }
        """;

        // Act
        var metadata = JsonSerializer.Deserialize<BackupMetadata>(json, JsonOptions);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal(1, metadata.MetadataVersion);
    }

    [Fact]
    public void Serialization_IncludesMetadataVersion()
    {
        // Arrange
        var metadata = new BackupMetadata
        {
            MetadataVersion = 2,
            OriginalFileName = "test.vma.zst",
            OriginalFileSize = 1000,
            OriginalFileHash = "abc123",
            TotalChunks = 1,
            ChunkSize = 49L * 1024 * 1024 * 1024,
            CreatedAt = DateTime.UtcNow,
            ProxmoxVersion = "8.0",
            VmType = "qemu",
            VmId = 100,
            VmName = "test-vm",
            NodeName = "pve"
        };

        // Act
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<BackupMetadata>(json, JsonOptions);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.MetadataVersion);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        // Arrange
        var original = new BackupMetadata
        {
            MetadataVersion = 2,
            OriginalFileName = "vzdump-qemu-100.vma.zst",
            OriginalFileSize = 50L * 1024 * 1024 * 1024,
            OriginalFileHash = "abc123def456",
            TotalChunks = 2,
            ChunkSize = 49L * 1024 * 1024 * 1024,
            CreatedAt = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            ProxmoxVersion = "8.1.3",
            VmType = "qemu",
            VmId = 100,
            VmName = "my-vm",
            NodeName = "pve-node1",
            Chunks =
            [
                new ChunkInfo
                {
                    Index = 0,
                    FileName = "vzdump-qemu-100.part0001.vma.zst",
                    Size = 49L * 1024 * 1024 * 1024,
                    Hash = "hash1",
                    Offset = 0
                },
                new ChunkInfo
                {
                    Index = 1,
                    FileName = "vzdump-qemu-100.part0002.vma.zst",
                    Size = 1L * 1024 * 1024 * 1024,
                    Hash = "hash2",
                    Offset = 49L * 1024 * 1024 * 1024
                }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<BackupMetadata>(json, JsonOptions);

        // Assert
        Assert.NotNull(restored);
        Assert.Equal(original.MetadataVersion, restored.MetadataVersion);
        Assert.Equal(original.OriginalFileName, restored.OriginalFileName);
        Assert.Equal(original.OriginalFileSize, restored.OriginalFileSize);
        Assert.Equal(original.OriginalFileHash, restored.OriginalFileHash);
        Assert.Equal(original.TotalChunks, restored.TotalChunks);
        Assert.Equal(original.ChunkSize, restored.ChunkSize);
        Assert.Equal(original.ProxmoxVersion, restored.ProxmoxVersion);
        Assert.Equal(original.VmType, restored.VmType);
        Assert.Equal(original.VmId, restored.VmId);
        Assert.Equal(original.VmName, restored.VmName);
        Assert.Equal(original.NodeName, restored.NodeName);
        Assert.Equal(original.Chunks.Count, restored.Chunks.Count);

        for (int i = 0; i < original.Chunks.Count; i++)
        {
            Assert.Equal(original.Chunks[i].Index, restored.Chunks[i].Index);
            Assert.Equal(original.Chunks[i].FileName, restored.Chunks[i].FileName);
            Assert.Equal(original.Chunks[i].Size, restored.Chunks[i].Size);
            Assert.Equal(original.Chunks[i].Hash, restored.Chunks[i].Hash);
            Assert.Equal(original.Chunks[i].Offset, restored.Chunks[i].Offset);
        }
    }
}
