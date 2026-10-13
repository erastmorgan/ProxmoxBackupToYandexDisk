using ProxmoxBackupToYandexDisk.Core.FileOperations;

namespace ProxmoxBackupToYandexDisk.Tests.FileOperations;

public class FileSplitterTests
{
    [Fact]
    public void NeedsSplitting_ReturnsFalse_ForSmallFiles()
    {
        // Arrange
        var smallSize = 10L * 1024 * 1024 * 1024; // 10 GB

        // Act & Assert
        Assert.False(FileSplitter.NeedsSplitting(smallSize));
    }

    [Fact]
    public void NeedsSplitting_ReturnsTrue_ForLargeFiles()
    {
        // Arrange
        var largeSize = 50L * 1024 * 1024 * 1024; // 50 GB

        // Act & Assert
        Assert.True(FileSplitter.NeedsSplitting(largeSize));
    }

    [Fact]
    public void NeedsSplitting_ReturnsFalse_AtExactLimit()
    {
        // Arrange
        var exactLimit = FileSplitter.MaxChunkSize; // 49 GB

        // Act & Assert
        Assert.False(FileSplitter.NeedsSplitting(exactLimit));
    }

    [Fact]
    public void NeedsSplitting_ReturnsTrue_JustOverLimit()
    {
        // Arrange
        var justOver = FileSplitter.MaxChunkSize + 1;

        // Act & Assert
        Assert.True(FileSplitter.NeedsSplitting(justOver));
    }

    [Fact]
    public void CalculateChunks_ReturnsSingleChunk_ForSmallFile()
    {
        // Arrange
        var fileName = "backup.vma.zst";
        var fileSize = 10L * 1024 * 1024 * 1024; // 10 GB

        // Act
        var chunks = FileSplitter.CalculateChunks(fileName, fileSize);

        // Assert
        Assert.Single(chunks);
        Assert.Equal(fileName, chunks[0].FileName);
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(fileSize, chunks[0].Size);
        Assert.Equal(0, chunks[0].Offset);
    }

    [Fact]
    public void CalculateChunks_ReturnsMultipleChunks_ForLargeFile()
    {
        // Arrange
        var fileName = "backup.vma.zst";
        var fileSize = 100L * 1024 * 1024 * 1024; // 100 GB

        // Act
        var chunks = FileSplitter.CalculateChunks(fileName, fileSize);

        // Assert
        Assert.Equal(3, chunks.Count); // 100 GB / 49 GB = 2.04, so 3 chunks

        // Verify chunk naming (Path.GetExtension returns only last extension)
        Assert.Equal("backup.vma.part0001.zst", chunks[0].FileName);
        Assert.Equal("backup.vma.part0002.zst", chunks[1].FileName);
        Assert.Equal("backup.vma.part0003.zst", chunks[2].FileName);

        // Verify indices
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(1, chunks[1].Index);
        Assert.Equal(2, chunks[2].Index);

        // Verify offsets
        Assert.Equal(0, chunks[0].Offset);
        Assert.Equal(FileSplitter.MaxChunkSize, chunks[1].Offset);
        Assert.Equal(FileSplitter.MaxChunkSize * 2, chunks[2].Offset);

        // Verify sizes
        Assert.Equal(FileSplitter.MaxChunkSize, chunks[0].Size);
        Assert.Equal(FileSplitter.MaxChunkSize, chunks[1].Size);
        Assert.Equal(fileSize - FileSplitter.MaxChunkSize * 2, chunks[2].Size);

        // Verify total size equals original
        Assert.Equal(fileSize, chunks.Sum(c => c.Size));
    }

    [Fact]
    public void CalculateChunks_HandlesExactMultiple()
    {
        // Arrange
        var fileName = "backup.vma.zst";
        var fileSize = FileSplitter.MaxChunkSize * 2; // Exactly 2 chunks

        // Act
        var chunks = FileSplitter.CalculateChunks(fileName, fileSize);

        // Assert
        Assert.Equal(2, chunks.Count);
        Assert.Equal(FileSplitter.MaxChunkSize, chunks[0].Size);
        Assert.Equal(FileSplitter.MaxChunkSize, chunks[1].Size);
    }

    [Fact]
    public void CalculateChunks_HashesAreEmptyStrings()
    {
        // Arrange
        var fileName = "backup.vma.zst";
        var fileSize = 100L * 1024 * 1024 * 1024;

        // Act
        var chunks = FileSplitter.CalculateChunks(fileName, fileSize);

        // Assert - hashes should be empty (computed during streaming upload)
        Assert.All(chunks, c => Assert.Equal("", c.Hash));
    }

    [Fact]
    public void CalculateChunks_PreservesExtension()
    {
        // Arrange
        var fileName = "vzdump-qemu-100-2024_01_15-12_00_00.vma.zst";
        var fileSize = FileSplitter.MaxChunkSize * 2 + 1; // 3 chunks

        // Act
        var chunks = FileSplitter.CalculateChunks(fileName, fileSize);

        // Assert (Path.GetExtension returns only last extension .zst)
        Assert.Equal("vzdump-qemu-100-2024_01_15-12_00_00.vma.part0001.zst", chunks[0].FileName);
        Assert.Equal("vzdump-qemu-100-2024_01_15-12_00_00.vma.part0002.zst", chunks[1].FileName);
        Assert.Equal("vzdump-qemu-100-2024_01_15-12_00_00.vma.part0003.zst", chunks[2].FileName);
    }
}
