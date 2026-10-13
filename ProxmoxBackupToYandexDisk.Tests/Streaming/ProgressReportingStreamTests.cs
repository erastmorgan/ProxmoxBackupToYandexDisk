using ProxmoxBackupToYandexDisk.Core.Streaming;

namespace ProxmoxBackupToYandexDisk.Tests.Streaming;

public class ProgressReportingStreamTests
{
    [Fact]
    public void Read_ReportsProgress()
    {
        // Arrange
        var data = new byte[100];
        using var innerStream = new MemoryStream(data);
        var reports = new List<(long BytesRead, long TotalBytes)>();
        var progress = new Progress<(long, long)>(p => reports.Add(p));
        using var progressStream = new ProgressReportingStream(innerStream, 100, progress);

        // Act
        var buffer = new byte[30];
        progressStream.Read(buffer, 0, 30);
        progressStream.Read(buffer, 0, 30);
        progressStream.Read(buffer, 0, 30);

        // Allow Progress<T> to process (it posts to synchronization context)
        Thread.Sleep(50);

        // Assert
        Assert.True(reports.Count >= 1);
        Assert.All(reports, r => Assert.Equal(100, r.TotalBytes));
    }

    [Fact]
    public async Task ReadAsync_ReportsProgress()
    {
        // Arrange
        var data = new byte[100];
        using var innerStream = new MemoryStream(data);
        var reports = new List<(long BytesRead, long TotalBytes)>();
        var progress = new Progress<(long, long)>(p => reports.Add(p));
        using var progressStream = new ProgressReportingStream(innerStream, 100, progress);

        // Act
        var buffer = new byte[50];
        await progressStream.ReadAsync(buffer, 0, 50);
        await progressStream.ReadAsync(buffer, 0, 50);

        // Allow Progress<T> to process
        await Task.Delay(50);

        // Assert
        Assert.True(reports.Count >= 1);
    }

    [Fact]
    public void Read_ReturnsCorrectData()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var innerStream = new MemoryStream(data);
        using var progressStream = new ProgressReportingStream(innerStream, data.Length, null);

        // Act
        var buffer = new byte[10];
        var bytesRead = progressStream.Read(buffer, 0, 10);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer.Take(5).ToArray());
    }

    [Fact]
    public void Position_TracksReadPosition()
    {
        // Arrange
        var data = new byte[100];
        using var innerStream = new MemoryStream(data);
        using var progressStream = new ProgressReportingStream(innerStream, 100, null);

        // Act & Assert
        Assert.Equal(0, progressStream.Position);

        var buffer = new byte[30];
        progressStream.Read(buffer, 0, 30);
        Assert.Equal(30, progressStream.Position);

        progressStream.Read(buffer, 0, 30);
        Assert.Equal(60, progressStream.Position);
    }

    [Fact]
    public void NullProgress_DoesNotThrow()
    {
        // Arrange
        var data = new byte[100];
        using var innerStream = new MemoryStream(data);
        using var progressStream = new ProgressReportingStream(innerStream, 100, null);

        // Act & Assert - should not throw
        var buffer = new byte[50];
        var bytesRead = progressStream.Read(buffer, 0, 50);
        Assert.Equal(50, bytesRead);
    }
}
