using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.Models;

namespace ProxmoxBackupToYandexDisk.Core.FileOperations;

/// <summary>
/// Service for splitting large files into chunks and reassembling them
/// </summary>
public sealed class FileSplitter(ILogger<FileSplitter> logger, FileHasher fileHasher)
{
    /// <summary>
    /// Maximum chunk size (50 GB - some margin for safety)
    /// </summary>
    public const long MaxChunkSize = 49L * 1024 * 1024 * 1024; // 49 GB

    // Large buffer for maximum I/O throughput
    private const int BufferSize = 16 * 1024 * 1024; // 16 MB buffer

    /// <summary>
    /// Splits a file into chunks if necessary.
    /// Optimized: computes hashes while reading/writing in a single pass.
    /// </summary>
    public async Task<(List<string> ChunkPaths, BackupMetadata Metadata)> SplitFileAsync(
        string sourceFilePath,
        string outputDirectory,
        BackupMetadata baseMetadata,
        IProgress<(long BytesProcessed, long TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(sourceFilePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Source file not found", sourceFilePath);

        var totalSize = fileInfo.Length;
        var chunks = new List<ChunkInfo>();
        var chunkPaths = new List<string>();

        // Calculate number of chunks needed
        var numChunks = (int)Math.Ceiling((double)totalSize / MaxChunkSize);

        logger.LogInformation(
            "Splitting file {FileName} ({Size:N0} bytes) into {NumChunks} chunk(s) - optimized single-pass",
            fileInfo.Name, totalSize, numChunks);

        // Use large buffer for better I/O performance
        var buffer = new byte[BufferSize];
        long totalBytesProcessed = 0;
        var baseFileName = Path.GetFileNameWithoutExtension(fileInfo.Name);
        var extension = fileInfo.Extension;

        // Compute original file hash AND split in single pass
        using var originalHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using var sourceStream = new FileStream(
            sourceFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        for (var i = 0; i < numChunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkOffset = i * MaxChunkSize;
            var chunkSize = Math.Min(MaxChunkSize, totalSize - chunkOffset);
            var chunkFileName = numChunks == 1
                ? fileInfo.Name
                : $"{baseFileName}.part{i + 1:D4}{extension}";
            var chunkPath = Path.Combine(outputDirectory, chunkFileName);

            logger.LogInformation(
                "Writing chunk {ChunkNum}/{TotalChunks}: {ChunkFileName} ({Size:N0} bytes)",
                i + 1, numChunks, chunkFileName, chunkSize);

            // Compute chunk hash while writing
            using var chunkHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var chunkStream = new FileStream(
                chunkPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long chunkBytesWritten = 0;
                while (chunkBytesWritten < chunkSize)
                {
                    var toRead = (int)Math.Min(buffer.Length, chunkSize - chunkBytesWritten);
                    var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);

                    if (bytesRead == 0)
                        break;

                    // Write to chunk file
                    await chunkStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                    // Update both hashes simultaneously (original file and chunk)
                    originalHasher.AppendData(buffer.AsSpan(0, bytesRead));
                    chunkHasher.AppendData(buffer.AsSpan(0, bytesRead));

                    chunkBytesWritten += bytesRead;
                    totalBytesProcessed += bytesRead;

                    progress?.Report((totalBytesProcessed, totalSize));
                }
            }

            // Get chunk hash
            var chunkHashBytes = chunkHasher.GetHashAndReset();
            var chunkHash = Convert.ToHexString(chunkHashBytes).ToLowerInvariant();

            chunks.Add(new ChunkInfo
            {
                Index = i,
                FileName = chunkFileName,
                Size = chunkSize,
                Hash = chunkHash,
                Offset = chunkOffset
            });

            chunkPaths.Add(chunkPath);
        }

        // Get original file hash
        var originalHashBytes = originalHasher.GetHashAndReset();
        var originalHash = Convert.ToHexString(originalHashBytes).ToLowerInvariant();

        var metadata = baseMetadata with
        {
            OriginalFileHash = originalHash,
            TotalChunks = numChunks,
            ChunkSize = MaxChunkSize,
            Chunks = chunks
        };

        logger.LogInformation("File split complete. Total chunks: {NumChunks}", numChunks);

        return (chunkPaths, metadata);
    }

    /// <summary>
    /// Reassembles chunks back into the original file
    /// </summary>
    public async Task<string> ReassembleFileAsync(
        BackupMetadata metadata,
        string chunksDirectory,
        string outputDirectory,
        IProgress<(long BytesProcessed, long TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outputPath = Path.Combine(outputDirectory, metadata.OriginalFileName);

        logger.LogInformation(
            "Reassembling {NumChunks} chunks into {OutputPath}",
            metadata.TotalChunks, outputPath);

        // Verify all chunks exist and have correct hashes
        logger.LogInformation("Verifying chunk integrity...");
        foreach (var chunk in metadata.Chunks.OrderBy(c => c.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkPath = Path.Combine(chunksDirectory, chunk.FileName);
            if (!File.Exists(chunkPath))
                throw new FileNotFoundException($"Chunk file not found: {chunk.FileName}", chunkPath);

            var isValid = await fileHasher.VerifyHashAsync(chunkPath, chunk.Hash, null, cancellationToken);
            if (!isValid)
                throw new InvalidDataException($"Chunk {chunk.FileName} failed hash verification");

            logger.LogDebug("Chunk {ChunkNum} verified: {FileName}", chunk.Index + 1, chunk.FileName);
        }

        // Reassemble the file with hash computation
        var buffer = new byte[BufferSize];
        long totalBytesWritten = 0;
        using var fileHasher2 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using (var outputStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            foreach (var chunk in metadata.Chunks.OrderBy(c => c.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkPath = Path.Combine(chunksDirectory, chunk.FileName);
                logger.LogInformation("Processing chunk {ChunkNum}/{TotalChunks}: {FileName}",
                    chunk.Index + 1, metadata.TotalChunks, chunk.FileName);

                await using var chunkStream = new FileStream(
                    chunkPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                int bytesRead;
                while ((bytesRead = await chunkStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    fileHasher2.AppendData(buffer.AsSpan(0, bytesRead));
                    totalBytesWritten += bytesRead;
                    progress?.Report((totalBytesWritten, metadata.OriginalFileSize));
                }
            }
        }

        // Verify hash computed during reassembly
        var computedHashBytes = fileHasher2.GetHashAndReset();
        var computedHash = Convert.ToHexString(computedHashBytes).ToLowerInvariant();

        if (!string.Equals(computedHash, metadata.OriginalFileHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Reassembled file failed hash verification. Expected: {metadata.OriginalFileHash}, Got: {computedHash}");

        logger.LogInformation("File reassembled and verified successfully: {OutputPath}", outputPath);
        return outputPath;
    }

    /// <summary>
    /// Checks if a file needs to be split (exceeds max chunk size)
    /// </summary>
    public static bool NeedsSplitting(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        return fileInfo.Exists && fileInfo.Length > MaxChunkSize;
    }

    /// <summary>
    /// Checks if a file needs to be split (exceeds max chunk size)
    /// </summary>
    public static bool NeedsSplitting(long fileSize)
    {
        return fileSize > MaxChunkSize;
    }

    /// <summary>
    /// Calculates chunk information for a file without creating actual chunk files.
    /// Used for streaming uploads where chunks are uploaded directly without intermediate files.
    /// </summary>
    public static List<ChunkInfo> CalculateChunks(string fileName, long totalSize)
    {
        var chunks = new List<ChunkInfo>();
        var numChunks = (int)Math.Ceiling((double)totalSize / MaxChunkSize);

        var baseFileName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 0; i < numChunks; i++)
        {
            var chunkOffset = i * MaxChunkSize;
            var chunkSize = Math.Min(MaxChunkSize, totalSize - chunkOffset);
            var chunkFileName = numChunks == 1
                ? fileName
                : $"{baseFileName}.part{i + 1:D4}{extension}";

            chunks.Add(new ChunkInfo
            {
                Index = i,
                FileName = chunkFileName,
                Size = chunkSize,
                Hash = "", // Will be computed during streaming upload
                Offset = chunkOffset
            });
        }

        return chunks;
    }
}
