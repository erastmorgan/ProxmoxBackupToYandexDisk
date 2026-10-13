namespace ProxmoxBackupToYandexDisk.Core.Streaming;

/// <summary>
/// A stream wrapper that passes all read data to one or more consumers.
/// Useful for computing hashes while streaming data to another destination.
/// </summary>
public sealed class TeeStream : Stream
{
    private readonly Stream _innerStream;
    private readonly IReadOnlyList<IStreamConsumer> _consumers;
    private readonly bool _leaveOpen;
    private bool _completed;

    /// <summary>
    /// Creates a new TeeStream.
    /// </summary>
    /// <param name="innerStream">The underlying stream to read from</param>
    /// <param name="consumers">Consumers that will receive all read data</param>
    /// <param name="leaveOpen">If true, the inner stream won't be disposed when this stream is disposed</param>
    public TeeStream(Stream innerStream, IReadOnlyList<IStreamConsumer> consumers, bool leaveOpen = false)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _consumers = consumers ?? throw new ArgumentNullException(nameof(consumers));
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Creates a new TeeStream with a single consumer.
    /// </summary>
    public TeeStream(Stream innerStream, IStreamConsumer consumer, bool leaveOpen = false)
        : this(innerStream, new[] { consumer }, leaveOpen)
    {
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);

        if (bytesRead > 0)
        {
            var span = new ReadOnlySpan<byte>(buffer, offset, bytesRead);
            foreach (var consumer in _consumers)
            {
                consumer.Consume(span);
            }
        }
        else if (!_completed)
        {
            CompleteConsumers();
        }

        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        if (bytesRead > 0)
        {
            var span = new ReadOnlySpan<byte>(buffer, offset, bytesRead);
            foreach (var consumer in _consumers)
            {
                consumer.Consume(span);
            }
        }
        else if (!_completed)
        {
            CompleteConsumers();
        }

        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);

        if (bytesRead > 0)
        {
            var span = buffer.Span[..bytesRead];
            foreach (var consumer in _consumers)
            {
                consumer.Consume(span);
            }
        }
        else if (!_completed)
        {
            CompleteConsumers();
        }

        return bytesRead;
    }

    /// <summary>
    /// Manually completes all consumers. Call this if the stream reading ends early.
    /// </summary>
    public void CompleteConsumers()
    {
        if (_completed)
            return;

        foreach (var consumer in _consumers)
        {
            consumer.Complete();
        }
        _completed = true;
    }

    public override void Flush() => _innerStream.Flush();

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

    public override void SetLength(long value) =>
        throw new NotSupportedException("SetLength is not supported");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Writing is not supported");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteConsumers();
            if (!_leaveOpen)
            {
                _innerStream.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        CompleteConsumers();
        if (!_leaveOpen)
        {
            await _innerStream.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}
