using System.Security.Cryptography;

namespace ProxmoxBackupToYandexDisk.Core.Streaming;

/// <summary>
/// Interface for consuming data from a TeeStream
/// </summary>
public interface IStreamConsumer
{
    /// <summary>
    /// Called when data is read from the stream
    /// </summary>
    void Consume(ReadOnlySpan<byte> data);

    /// <summary>
    /// Called when all data has been consumed
    /// </summary>
    void Complete();
}

/// <summary>
/// A stream consumer that computes SHA-256 hash incrementally as data flows through.
/// </summary>
public sealed class HashingStreamConsumer : IStreamConsumer, IDisposable
{
    private readonly IncrementalHash _hash;
    private string? _finalHash;
    private bool _completed;

    public HashingStreamConsumer()
    {
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Gets the computed hash after Complete() is called.
    /// Returns null if not yet completed.
    /// </summary>
    public string? Hash => _finalHash;

    /// <summary>
    /// Whether the hashing has been completed
    /// </summary>
    public bool IsCompleted => _completed;

    public void Consume(ReadOnlySpan<byte> data)
    {
        if (_completed)
            throw new InvalidOperationException("Cannot consume data after completion");

        _hash.AppendData(data);
    }

    public void Complete()
    {
        if (_completed)
            return;

        var hashBytes = _hash.GetHashAndReset();
        _finalHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        _completed = true;
    }

    public void Dispose()
    {
        _hash.Dispose();
    }
}
