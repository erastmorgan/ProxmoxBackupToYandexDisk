namespace ProxmoxBackupToYandexDisk.Core.Streaming;

/// <summary>
/// A stream wrapper that limits reading to a specified number of bytes.
/// Used for reading chunks from a larger stream without consuming more data than needed.
/// </summary>
public sealed class LimitedReadStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _maxBytes;
    private readonly bool _leaveOpen;
    private long _bytesRead;

    /// <summary>
    /// Creates a new LimitedReadStream.
    /// </summary>
    /// <param name="innerStream">The underlying stream to read from</param>
    /// <param name="maxBytes">Maximum number of bytes to allow reading</param>
    /// <param name="leaveOpen">If true, the inner stream won't be disposed when this stream is disposed</param>
    public LimitedReadStream(Stream innerStream, long maxBytes, bool leaveOpen = false)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _maxBytes = maxBytes;
        _leaveOpen = leaveOpen;
        _bytesRead = 0;
    }

    /// <summary>
    /// Number of bytes remaining before the limit is reached
    /// </summary>
    public long BytesRemaining => Math.Max(0, _maxBytes - _bytesRead);

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _maxBytes;
    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException("Seeking is not supported");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_bytesRead >= _maxBytes)
            return 0;

        var toRead = (int)Math.Min(count, _maxBytes - _bytesRead);
        var bytesRead = _innerStream.Read(buffer, offset, toRead);
        _bytesRead += bytesRead;
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_bytesRead >= _maxBytes)
            return 0;

        var toRead = (int)Math.Min(count, _maxBytes - _bytesRead);
        var bytesRead = await _innerStream.ReadAsync(buffer.AsMemory(offset, toRead), cancellationToken);
        _bytesRead += bytesRead;
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_bytesRead >= _maxBytes)
            return 0;

        var toRead = (int)Math.Min(buffer.Length, _maxBytes - _bytesRead);
        var bytesRead = await _innerStream.ReadAsync(buffer[..toRead], cancellationToken);
        _bytesRead += bytesRead;
        return bytesRead;
    }

    public override void Flush() => _innerStream.Flush();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Seeking is not supported");

    public override void SetLength(long value) =>
        throw new NotSupportedException("SetLength is not supported");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Writing is not supported");

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _innerStream.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}
