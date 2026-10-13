using ProxmoxBackupToYandexDisk.Core.Streaming;

namespace ProxmoxBackupToYandexDisk.Tests.Streaming;

public class TeeStreamTests
{
    [Fact]
    public void Read_PassesDataToConsumer()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var innerStream = new MemoryStream(data);
        var consumer = new TestConsumer();
        using var teeStream = new TeeStream(innerStream, consumer);

        // Act
        var buffer = new byte[10];
        var bytesRead = teeStream.Read(buffer, 0, 10);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, consumer.ReceivedData.ToArray());
    }

    [Fact]
    public async Task ReadAsync_PassesDataToConsumer()
    {
        // Arrange
        var data = new byte[] { 10, 20, 30, 40, 50 };
        using var innerStream = new MemoryStream(data);
        var consumer = new TestConsumer();
        using var teeStream = new TeeStream(innerStream, consumer);

        // Act
        var buffer = new byte[10];
        var bytesRead = await teeStream.ReadAsync(buffer, 0, 10);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, consumer.ReceivedData.ToArray());
    }

    [Fact]
    public void Read_PassesDataToMultipleConsumers()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3 };
        using var innerStream = new MemoryStream(data);
        var consumer1 = new TestConsumer();
        var consumer2 = new TestConsumer();
        using var teeStream = new TeeStream(innerStream, new[] { consumer1, consumer2 });

        // Act
        var buffer = new byte[10];
        teeStream.Read(buffer, 0, 10);

        // Assert
        Assert.Equal(data, consumer1.ReceivedData.ToArray());
        Assert.Equal(data, consumer2.ReceivedData.ToArray());
    }

    [Fact]
    public void Read_CompletesConsumerOnEndOfStream()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3 };
        using var innerStream = new MemoryStream(data);
        var consumer = new TestConsumer();
        using var teeStream = new TeeStream(innerStream, consumer);

        // Act
        var buffer = new byte[10];
        teeStream.Read(buffer, 0, 10); // Read all data
        teeStream.Read(buffer, 0, 10); // Read returns 0 - triggers completion

        // Assert
        Assert.True(consumer.IsCompleted);
    }

    [Fact]
    public void Dispose_CompletesConsumer()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var innerStream = new MemoryStream(data);
        var consumer = new TestConsumer();
        var teeStream = new TeeStream(innerStream, consumer);

        // Act
        var buffer = new byte[2];
        teeStream.Read(buffer, 0, 2); // Read only partial data
        teeStream.Dispose();

        // Assert
        Assert.True(consumer.IsCompleted);
    }

    private class TestConsumer : IStreamConsumer
    {
        public List<byte> ReceivedData { get; } = new();
        public bool IsCompleted { get; private set; }

        public void Consume(ReadOnlySpan<byte> data)
        {
            ReceivedData.AddRange(data.ToArray());
        }

        public void Complete()
        {
            IsCompleted = true;
        }
    }
}
