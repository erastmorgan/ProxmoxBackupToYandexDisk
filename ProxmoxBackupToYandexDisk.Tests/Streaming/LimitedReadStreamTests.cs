using ProxmoxBackupToYandexDisk.Core.Streaming;

namespace ProxmoxBackupToYandexDisk.Tests.Streaming;

public class LimitedReadStreamTests
{
    [Fact]
    public void Read_LimitsToMaxBytes()
    {
        // Arrange
        var data = new byte[100];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;

        using var innerStream = new MemoryStream(data);
        using var limitedStream = new LimitedReadStream(innerStream, maxBytes: 50);

        // Act
        var buffer = new byte[100];
        var totalRead = 0;
        int bytesRead;
        while ((bytesRead = limitedStream.Read(buffer, totalRead, buffer.Length - totalRead)) > 0)
        {
            totalRead += bytesRead;
        }

        // Assert
        Assert.Equal(50, totalRead);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal((byte)i, buffer[i]);
        }
    }

    [Fact]
    public async Task ReadAsync_LimitsToMaxBytes()
    {
        // Arrange
        var data = new byte[100];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;

        using var innerStream = new MemoryStream(data);
        using var limitedStream = new LimitedReadStream(innerStream, maxBytes: 30);

        // Act
        var buffer = new byte[100];
        var totalRead = 0;
        int bytesRead;
        while ((bytesRead = await limitedStream.ReadAsync(buffer, totalRead, buffer.Length - totalRead)) > 0)
        {
            totalRead += bytesRead;
        }

        // Assert
        Assert.Equal(30, totalRead);
    }

    [Fact]
    public void BytesRemaining_DecreasesAsDataIsRead()
    {
        // Arrange
        var data = new byte[100];
        using var innerStream = new MemoryStream(data);
        using var limitedStream = new LimitedReadStream(innerStream, maxBytes: 50);

        // Act & Assert
        Assert.Equal(50, limitedStream.BytesRemaining);

        var buffer = new byte[20];
        limitedStream.Read(buffer, 0, 20);
        Assert.Equal(30, limitedStream.BytesRemaining);

        limitedStream.Read(buffer, 0, 20);
        Assert.Equal(10, limitedStream.BytesRemaining);

        limitedStream.Read(buffer, 0, 20);
        Assert.Equal(0, limitedStream.BytesRemaining);
    }

    [Fact]
    public void Length_ReturnsMaxBytes()
    {
        // Arrange
        var data = new byte[100];
        using var innerStream = new MemoryStream(data);
        using var limitedStream = new LimitedReadStream(innerStream, maxBytes: 42);

        // Assert
        Assert.Equal(42, limitedStream.Length);
    }

    [Fact]
    public void Read_ReturnsZeroWhenLimitReached()
    {
        // Arrange
        var data = new byte[100];
        using var innerStream = new MemoryStream(data);
        using var limitedStream = new LimitedReadStream(innerStream, maxBytes: 10);

        // Act
        var buffer = new byte[20];
        var firstRead = limitedStream.Read(buffer, 0, 20);
        var secondRead = limitedStream.Read(buffer, 0, 20);

        // Assert
        Assert.Equal(10, firstRead);
        Assert.Equal(0, secondRead);
    }

    [Fact]
    public void LeaveOpen_DoesNotDisposeInnerStream()
    {
        // Arrange
        var innerStream = new MemoryStream(new byte[100]);
        var limitedStream = new LimitedReadStream(innerStream, maxBytes: 50, leaveOpen: true);

        // Act
        limitedStream.Dispose();

        // Assert - inner stream should still be usable
        Assert.True(innerStream.CanRead);
        innerStream.Dispose();
    }
}
