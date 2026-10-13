using System.Security.Cryptography;
using ProxmoxBackupToYandexDisk.Core.Streaming;

namespace ProxmoxBackupToYandexDisk.Tests.Streaming;

public class HashingStreamConsumerTests
{
    [Fact]
    public void ComputesCorrectHash_ForSimpleData()
    {
        // Arrange
        var data = "Hello, World!"u8.ToArray();
        using var consumer = new HashingStreamConsumer();

        // Act
        consumer.Consume(data);
        consumer.Complete();

        // Assert
        var expectedHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Assert.Equal(expectedHash, consumer.Hash);
    }

    [Fact]
    public void ComputesCorrectHash_ForMultipleChunks()
    {
        // Arrange
        var chunk1 = "Hello, "u8.ToArray();
        var chunk2 = "World!"u8.ToArray();
        var fullData = "Hello, World!"u8.ToArray();
        using var consumer = new HashingStreamConsumer();

        // Act
        consumer.Consume(chunk1);
        consumer.Consume(chunk2);
        consumer.Complete();

        // Assert
        var expectedHash = Convert.ToHexString(SHA256.HashData(fullData)).ToLowerInvariant();
        Assert.Equal(expectedHash, consumer.Hash);
    }

    [Fact]
    public void Hash_IsNullBeforeComplete()
    {
        // Arrange
        using var consumer = new HashingStreamConsumer();
        consumer.Consume("test"u8);

        // Assert
        Assert.Null(consumer.Hash);
        Assert.False(consumer.IsCompleted);
    }

    [Fact]
    public void Complete_SetsIsCompleted()
    {
        // Arrange
        using var consumer = new HashingStreamConsumer();

        // Act
        consumer.Complete();

        // Assert
        Assert.True(consumer.IsCompleted);
    }

    [Fact]
    public void Consume_ThrowsAfterComplete()
    {
        // Arrange
        using var consumer = new HashingStreamConsumer();
        consumer.Complete();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => consumer.Consume("test"u8));
    }

    [Fact]
    public void ComputesCorrectHash_ForEmptyData()
    {
        // Arrange
        using var consumer = new HashingStreamConsumer();

        // Act
        consumer.Complete();

        // Assert
        var expectedHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        Assert.Equal(expectedHash, consumer.Hash);
    }

    [Fact]
    public void ComputesCorrectHash_ForLargeData()
    {
        // Arrange
        var data = new byte[1024 * 1024]; // 1 MB
        new Random(42).NextBytes(data);
        using var consumer = new HashingStreamConsumer();

        // Act - consume in chunks
        var offset = 0;
        var chunkSize = 8192;
        while (offset < data.Length)
        {
            var size = Math.Min(chunkSize, data.Length - offset);
            consumer.Consume(data.AsSpan(offset, size));
            offset += size;
        }
        consumer.Complete();

        // Assert
        var expectedHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Assert.Equal(expectedHash, consumer.Hash);
    }

    [Fact]
    public void IntegrationWithTeeStream_ComputesCorrectHash()
    {
        // Arrange
        var data = "Test data for hashing via TeeStream"u8.ToArray();
        using var innerStream = new MemoryStream(data);
        using var hashConsumer = new HashingStreamConsumer();
        using var teeStream = new TeeStream(innerStream, hashConsumer);

        // Act
        var buffer = new byte[1024];
        while (teeStream.Read(buffer, 0, buffer.Length) > 0) { }

        // Assert
        var expectedHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Assert.Equal(expectedHash, hashConsumer.Hash);
    }
}
