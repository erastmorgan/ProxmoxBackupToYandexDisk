namespace ProxmoxBackupToYandexDisk.Core.Streaming;

/// <summary>
/// A stream that supports seek to the beginning by reopening the underlying source.
/// This is useful for HTTP retry scenarios where the entire stream needs to be re-sent.
/// </summary>
public sealed class RetryableUploadStream : Stream
{
    private readonly IStreamSource _source;
    private readonly long _offset;
    private readonly long _length;
    private readonly CancellationToken _cancellationToken;
    private Stream? _currentStream;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Creates a new RetryableUploadStream.
    /// </summary>
    /// <param name="source">The stream source that can create new streams</param>
    /// <param name="offset">Starting offset within the source</param>
    /// <param name="length">Total length to read from the source</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    public RetryableUploadStream(
        IStreamSource source,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _offset = offset;
        _length = length;
        _cancellationToken = cancellationToken;
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true; // We support seeking to position 0
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value != 0)
                throw new NotSupportedException("Only seeking to position 0 is supported for retry scenarios");

            SeekToBeginning();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        EnsureStreamOpen();

        var remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        var toRead = (int)Math.Min(count, remaining);
        var bytesRead = _currentStream!.Read(buffer, offset, toRead);
        _position += bytesRead;
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await EnsureStreamOpenAsync();

        var remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        var toRead = (int)Math.Min(count, remaining);
        var bytesRead = await _currentStream!.ReadAsync(buffer.AsMemory(offset, toRead), cancellationToken);
        _position += bytesRead;
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await EnsureStreamOpenAsync();

        var remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        var toRead = (int)Math.Min(buffer.Length, remaining);
        var bytesRead = await _currentStream!.ReadAsync(buffer[..toRead], cancellationToken);
        _position += bytesRead;
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
        };

        if (newPosition != 0)
            throw new NotSupportedException("Only seeking to position 0 is supported for retry scenarios");

        SeekToBeginning();
        return 0;
    }

    private void SeekToBeginning()
    {
        // Close current stream and reset position
        // Next read will open a new stream
        _currentStream?.Dispose();
        _currentStream = null;
        _position = 0;
    }

    private void EnsureStreamOpen()
    {
        if (_currentStream == null)
        {
            _currentStream = _source.OpenAsync(_offset + _position, _cancellationToken)
                .GetAwaiter().GetResult();
        }
    }

    private async Task EnsureStreamOpenAsync()
    {
        if (_currentStream == null)
        {
            _currentStream = await _source.OpenAsync(_offset + _position, _cancellationToken);
        }
    }

    public override void Flush() { }

    public override void SetLength(long value) =>
        throw new NotSupportedException("SetLength is not supported");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Writing is not supported");

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _currentStream?.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (_currentStream != null)
            {
                await _currentStream.DisposeAsync();
            }
            _disposed = true;
        }
        await base.DisposeAsync();
    }
}
