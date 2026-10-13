using ProxmoxBackupToYandexDisk.Core.Streaming;

namespace ProxmoxBackupToYandexDisk.Tests.Streaming;

public class LocalFileStreamSourceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _tempFiles = new();

    public LocalFileStreamSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"streaming_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file)) File.Delete(file);
        }
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private string CreateTempFile(byte[] content)
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void TotalLength_ReturnsFileSize()
    {
        // Arrange
        var data = new byte[1234];
        var path = CreateTempFile(data);

        // Act
        var source = new LocalFileStreamSource(path);

        // Assert
        Assert.Equal(1234, source.TotalLength);
    }

    [Fact]
    public void FileName_ReturnsFileNameOnly()
    {
        // Arrange
        var path = CreateTempFile(new byte[100]);

        // Act
        var source = new LocalFileStreamSource(path);

        // Assert
        Assert.Equal(Path.GetFileName(path), source.FileName);
    }

    [Fact]
    public async Task OpenAsync_ReturnsReadableStream()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var path = CreateTempFile(data);
        var source = new LocalFileStreamSource(path);

        // Act
        await using var stream = await source.OpenAsync();
        var buffer = new byte[10];
        var bytesRead = await stream.ReadAsync(buffer);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer.Take(5).ToArray());
    }

    [Fact]
    public async Task OpenAsync_WithOffset_StartsFromOffset()
    {
        // Arrange
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var path = CreateTempFile(data);
        var source = new LocalFileStreamSource(path);

        // Act
        await using var stream = await source.OpenAsync(offset: 5);
        var buffer = new byte[10];
        var bytesRead = await stream.ReadAsync(buffer);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9 }, buffer.Take(5).ToArray());
    }

    [Fact]
    public async Task OpenAsync_CanOpenMultipleStreams()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var path = CreateTempFile(data);
        var source = new LocalFileStreamSource(path);

        // Act
        await using var stream1 = await source.OpenAsync();
        await using var stream2 = await source.OpenAsync();

        var buffer1 = new byte[10];
        var buffer2 = new byte[10];
        await stream1.ReadAsync(buffer1);
        await stream2.ReadAsync(buffer2);

        // Assert
        Assert.Equal(data, buffer1.Take(5).ToArray());
        Assert.Equal(data, buffer2.Take(5).ToArray());
    }

    [Fact]
    public void Constructor_ThrowsForNonExistentFile()
    {
        // Act & Assert
        Assert.Throws<FileNotFoundException>(() =>
            new LocalFileStreamSource("/nonexistent/path/file.bin"));
    }
}
