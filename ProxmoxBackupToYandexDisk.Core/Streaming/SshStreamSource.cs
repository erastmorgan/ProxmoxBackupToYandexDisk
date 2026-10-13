using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace ProxmoxBackupToYandexDisk.Core.Streaming;

/// <summary>
/// IStreamSource implementation for files on a remote SSH server.
/// Creates a new SFTP connection for each stream to support retry scenarios.
/// </summary>
public sealed class SshStreamSource : IStreamSource, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _remotePath;
    private readonly ILogger? _logger;
    private long? _cachedLength;

    public SshStreamSource(
        string host,
        int port,
        string username,
        string password,
        string remotePath,
        ILogger? logger = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _username = username ?? throw new ArgumentNullException(nameof(username));
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _remotePath = remotePath ?? throw new ArgumentNullException(nameof(remotePath));
        _port = port;
        _logger = logger;
    }

    public long TotalLength
    {
        get
        {
            if (!_cachedLength.HasValue)
            {
                using var client = CreateClient();
                client.Connect();
                var attrs = client.GetAttributes(_remotePath);
                _cachedLength = attrs.Size;
                client.Disconnect();
            }
            return _cachedLength.Value;
        }
    }

    public string FileName => Path.GetFileName(_remotePath);

    public Task<Stream> OpenAsync(long offset = 0, CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("Opening SSH stream for {Path} at offset {Offset}", _remotePath, offset);

        // Create a new SFTP client for this stream
        var client = CreateClient();
        client.Connect();

        Stream stream = client.OpenRead(_remotePath);

        // Wrap in a disposing stream that will clean up the client
        var wrappedStream = new SshDisposingStream(stream, client, _logger);

        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return Task.FromResult<Stream>(wrappedStream);
    }

    private SftpClient CreateClient()
    {
        return new SftpClient(_host, _port, _username, _password);
    }

    public void Dispose()
    {
        // No persistent resources to dispose
    }

    /// <summary>
    /// Stream wrapper that disposes the SFTP client when the stream is disposed.
    /// </summary>
    private sealed class SshDisposingStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly SftpClient _client;
        private readonly ILogger? _logger;
        private bool _disposed;

        public SshDisposingStream(Stream innerStream, SftpClient client, ILogger? logger)
        {
            _innerStream = innerStream;
            _client = client;
            _logger = logger;
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

        public override int Read(byte[] buffer, int offset, int count) =>
            _innerStream.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _innerStream.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _innerStream.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            _innerStream.Seek(offset, origin);

        public override void Flush() => _innerStream.Flush();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        _innerStream.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Error disposing SSH stream");
                    }

                    try
                    {
                        _client.Disconnect();
                        _client.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Error disposing SSH client");
                    }
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                try
                {
                    await _innerStream.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error disposing SSH stream");
                }

                try
                {
                    _client.Disconnect();
                    _client.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error disposing SSH client");
                }

                _disposed = true;
            }
            await base.DisposeAsync();
        }
    }
}
