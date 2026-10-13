using ProxmoxBackupToYandexDisk.Core.Streaming;

namespace ProxmoxBackupToYandexDisk.Tests.Streaming;

public class RetryableUploadStreamTests
{
    [Fact]
    public async Task Read_ReadsDataFromSource()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var source = new TestStreamSource(data);
        await using var stream = new RetryableUploadStream(source, offset: 0, length: 5);

        // Act
        var buffer = new byte[10];
        var bytesRead = await stream.ReadAsync(buffer);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer.Take(5).ToArray());
    }

    [Fact]
    public async Task Read_RespectsOffset()
    {
        // Arrange
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var source = new TestStreamSource(data);
        await using var stream = new RetryableUploadStream(source, offset: 3, length: 5);

        // Act
        var buffer = new byte[10];
        var bytesRead = await stream.ReadAsync(buffer);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7 }, buffer.Take(5).ToArray());
    }

    [Fact]
    public async Task Read_RespectsLength()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var source = new TestStreamSource(data);
        await using var stream = new RetryableUploadStream(source, offset: 0, length: 5);

        // Act
        var buffer = new byte[20];
        var totalRead = 0;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead)) > 0)
        {
            totalRead += bytesRead;
        }

        // Assert
        Assert.Equal(5, totalRead);
    }

    [Fact]
    public async Task Seek_ToZero_AllowsReread()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var source = new TestStreamSource(data);
        await using var stream = new RetryableUploadStream(source, offset: 0, length: 5);

        // Act
        var buffer1 = new byte[10];
        await stream.ReadAsync(buffer1);

        stream.Seek(0, SeekOrigin.Begin); // Reset to beginning

        var buffer2 = new byte[10];
        var bytesRead = await stream.ReadAsync(buffer2);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer2.Take(5).ToArray());
    }

    [Fact]
    public async Task Position_SetToZero_AllowsReread()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var source = new TestStreamSource(data);
        await using var stream = new RetryableUploadStream(source, offset: 0, length: 5);

        // Act
        var buffer1 = new byte[10];
        await stream.ReadAsync(buffer1);

        stream.Position = 0; // Reset to beginning

        var buffer2 = new byte[10];
        var bytesRead = await stream.ReadAsync(buffer2);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer2.Take(5).ToArray());
    }

    [Fact]
    public async Task Seek_ToNonZero_Throws()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var source = new TestStreamSource(data);
        await using var stream = new RetryableUploadStream(source, offset: 0, length: 5);

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.Seek(1, SeekOrigin.Begin));
    }

    [Fact]
    public void Length_ReturnsSpecifiedLength()
    {
        // Arrange
        var data = new byte[100];
        var source = new TestStreamSource(data);
        using var stream = new RetryableUploadStream(source, offset: 10, length: 50);

        // Assert
        Assert.Equal(50, stream.Length);
    }

    [Fact]
    public async Task Seek_ReopensStream()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var source = new TestStreamSource(data);
        await using var stream = new RetryableUploadStream(source, offset: 0, length: 5);

        // Act
        var buffer = new byte[10];
        await stream.ReadAsync(buffer);
        var openCountBefore = source.OpenCount;

        stream.Seek(0, SeekOrigin.Begin);
        await stream.ReadAsync(buffer);

        // Assert - should have opened a new stream
        Assert.Equal(openCountBefore + 1, source.OpenCount);
    }

    private class TestStreamSource : IStreamSource
    {
        private readonly byte[] _data;
        public int OpenCount { get; private set; }

        public TestStreamSource(byte[] data)
        {
            _data = data;
        }

        public long TotalLength => _data.Length;
        public string FileName => "test.bin";

        public Task<Stream> OpenAsync(long offset = 0, CancellationToken cancellationToken = default)
        {
            OpenCount++;
            var stream = new MemoryStream(_data);
            if (offset > 0)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }
            return Task.FromResult<Stream>(stream);
        }
    }
}
