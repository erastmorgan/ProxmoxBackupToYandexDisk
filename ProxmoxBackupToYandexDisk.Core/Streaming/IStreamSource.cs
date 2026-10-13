namespace ProxmoxBackupToYandexDisk.Core.Streaming;

/// <summary>
/// Abstraction for a data source that can provide streams for reading.
/// Supports reopening the stream for retry scenarios.
/// </summary>
public interface IStreamSource
{
    /// <summary>
    /// Total length of the data in bytes
    /// </summary>
    long TotalLength { get; }

    /// <summary>
    /// Name of the file or resource
    /// </summary>
    string FileName { get; }

    /// <summary>
    /// Opens a stream for reading, optionally starting from a specific offset.
    /// Each call creates a new stream that can be used independently.
    /// </summary>
    /// <param name="offset">Starting offset in bytes (default: 0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A stream positioned at the specified offset</returns>
    Task<Stream> OpenAsync(long offset = 0, CancellationToken cancellationToken = default);
}
