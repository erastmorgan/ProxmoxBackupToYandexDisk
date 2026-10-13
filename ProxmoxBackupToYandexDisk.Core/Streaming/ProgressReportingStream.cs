namespace ProxmoxBackupToYandexDisk.Core.Streaming;

/// <summary>
/// Stream wrapper that reports read progress.
/// </summary>
public sealed class ProgressReportingStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _totalBytes;
    private readonly IProgress<(long BytesRead, long TotalBytes)>? _progress;
    private readonly bool _leaveOpen;
    private long _bytesRead;

    /// <summary>
    /// Creates a new ProgressReportingStream.
    /// </summary>
    /// <param name="innerStream">The underlying stream to read from</param>
    /// <param name="totalBytes">Total number of bytes (for progress reporting)</param>
    /// <param name="progress">Progress reporter (receives tuple of bytes read and total bytes)</param>
    /// <param name="leaveOpen">If true, the inner stream won't be disposed when this stream is disposed</param>
    public ProgressReportingStream(
        Stream innerStream,
        long totalBytes,
        IProgress<(long BytesRead, long TotalBytes)>? progress,
        bool leaveOpen = false)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _totalBytes = totalBytes;
        _progress = progress;
        _leaveOpen = leaveOpen;
        _bytesRead = 0;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set
        {
            _innerStream.Position = value;
            _bytesRead = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        _bytesRead += bytesRead;
        _progress?.Report((_bytesRead, _totalBytes));
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        _bytesRead += bytesRead;
        _progress?.Report((_bytesRead, _totalBytes));
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);
        _bytesRead += bytesRead;
        _progress?.Report((_bytesRead, _totalBytes));
        return bytesRead;
    }

    public override void Flush() => _innerStream.Flush();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = _innerStream.Seek(offset, origin);
        _bytesRead = newPosition;
        return newPosition;
    }

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
