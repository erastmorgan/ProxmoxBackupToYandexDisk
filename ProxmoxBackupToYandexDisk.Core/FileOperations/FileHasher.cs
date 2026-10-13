using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace ProxmoxBackupToYandexDisk.Core.FileOperations;

/// <summary>
/// Service for computing file hashes for integrity verification
/// </summary>
public sealed class FileHasher(ILogger<FileHasher> logger)
{
    private const int BufferSize = 81920; // 80 KB buffer for streaming

    /// <summary>
    /// Computes SHA256 hash of a file
    /// </summary>
    public async Task<string> ComputeHashAsync(
        string filePath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Computing SHA256 hash for file: {FilePath}", filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ComputeHashAsync(stream, progress, cancellationToken);
    }

    /// <summary>
    /// Computes SHA256 hash of a stream
    /// </summary>
    public async Task<string> ComputeHashAsync(
        Stream stream,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var sha256 = SHA256.Create();
        var buffer = new byte[BufferSize];
        long totalBytesRead = 0;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, buffer, 0);
            totalBytesRead += bytesRead;
            progress?.Report(totalBytesRead);
        }

        sha256.TransformFinalBlock([], 0, 0);
        var hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();

        logger.LogDebug("Computed hash: {Hash}, Total bytes: {TotalBytes}", hash, totalBytesRead);
        return hash;
    }

    /// <summary>
    /// Computes SHA256 hash of a specific portion of a file
    /// </summary>
    public async Task<string> ComputeHashAsync(
        string filePath,
        long offset,
        long length,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Computing SHA256 hash for file chunk: {FilePath}, Offset: {Offset}, Length: {Length}",
            filePath, offset, length);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        stream.Seek(offset, SeekOrigin.Begin);

        using var sha256 = SHA256.Create();
        var buffer = new byte[BufferSize];
        long totalBytesRead = 0;
        long remaining = length;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);

            if (bytesRead == 0)
                break;

            sha256.TransformBlock(buffer, 0, bytesRead, buffer, 0);
            totalBytesRead += bytesRead;
            remaining -= bytesRead;
            progress?.Report(totalBytesRead);
        }

        sha256.TransformFinalBlock([], 0, 0);
        var hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();

        logger.LogDebug("Computed chunk hash: {Hash}, Bytes read: {TotalBytes}", hash, totalBytesRead);
        return hash;
    }

    /// <summary>
    /// Verifies that a file matches an expected hash
    /// </summary>
    public async Task<bool> VerifyHashAsync(
        string filePath,
        string expectedHash,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var actualHash = await ComputeHashAsync(filePath, progress, cancellationToken);
        var match = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);

        if (!match)
        {
            logger.LogWarning(
                "Hash mismatch for file {FilePath}. Expected: {Expected}, Actual: {Actual}",
                filePath, expectedHash, actualHash);
        }

        return match;
    }
}
