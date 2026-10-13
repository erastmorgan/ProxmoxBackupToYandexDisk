using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.FileOperations;
using ProxmoxBackupToYandexDisk.Core.Models;
using ProxmoxBackupToYandexDisk.Core.Proxmox;
using ProxmoxBackupToYandexDisk.Core.Streaming;
using ProxmoxBackupToYandexDisk.Core.YandexDisk;

namespace ProxmoxBackupToYandexDisk.Core.Services;

/// <summary>
/// Main service that orchestrates the backup process from Proxmox to Yandex Disk
/// </summary>
public sealed class BackupService(
    ILogger<BackupService> logger,
    ProxmoxService proxmoxService,
    YandexDiskService yandexDiskService,
    FileSplitter fileSplitter,
    FileHasher fileHasher,
    SshDownloadService? sshService = null)
{
    private const string MetadataFileName = "backup_metadata.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a backup of a VM/CT and uploads it to Yandex Disk
    /// </summary>
    public async Task<bool> BackupAndUploadAsync(
        string node,
        int vmId,
        string storage,
        string yandexDiskFolder,
        string? backupMode = "snapshot",
        string? compress = "zstd",
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get VM info
            progress?.Report(new BackupProgress("Getting VM information...", 0));
            var allVms = await proxmoxService.GetAllVirtualMachinesAsync(node);
            var vm = allVms.FirstOrDefault(v => v.VmId == vmId);

            if (vm == null)
            {
                logger.LogError("VM {VmId} not found on node {Node}", vmId, node);
                return false;
            }

            logger.LogInformation("Starting backup for {VmType} {VmId} ({VmName})",
                vm.Type, vmId, vm.Name);

            // Get Proxmox version
            var proxmoxVersion = await proxmoxService.GetVersionAsync() ?? "unknown";

            // Create backup on Proxmox
            progress?.Report(new BackupProgress("Creating Proxmox backup...", 5));
            var upid = await proxmoxService.CreateBackupAsync(
                node, vmId, storage,
                mode: backupMode ?? "snapshot",
                compress: compress ?? "zstd");

            if (string.IsNullOrEmpty(upid))
            {
                logger.LogError("Failed to start backup task");
                return false;
            }

            // Wait for backup to complete
            progress?.Report(new BackupProgress("Waiting for backup to complete...", 10));
            var backupSuccess = await proxmoxService.WaitForTaskAsync(
                node, upid,
                progress: new Progress<string>(msg => progress?.Report(new BackupProgress(msg, 15))),
                cancellationToken: cancellationToken);

            if (!backupSuccess)
            {
                logger.LogError("Backup task failed");
                return false;
            }

            // Find the created backup
            progress?.Report(new BackupProgress("Finding backup file...", 20));
            var backups = await proxmoxService.GetBackupsAsync(node, storage, vmId);
            var latestBackup = backups
                .OrderByDescending(b => b.CTime)
                .FirstOrDefault();

            if (latestBackup == null)
            {
                logger.LogError("Could not find the created backup");
                return false;
            }

            // Get backup file path
            var backupFilePath = await ResolveBackupPathAsync(node, storage, latestBackup.VolId);
            if (string.IsNullOrEmpty(backupFilePath))
            {
                logger.LogError("Could not determine backup file path");
                return false;
            }

            // Check if file is accessible locally or needs SSH download
            string localBackupPath;
            long fileSize;
            var tempDownloadDir = Path.Combine(Path.GetTempPath(), $"proxmox_backup_download_{Guid.NewGuid():N}");
            var needsCleanup = false;

            if (File.Exists(backupFilePath))
            {
                // Local file access (same machine as Proxmox)
                localBackupPath = backupFilePath;
                fileSize = new FileInfo(backupFilePath).Length;
                logger.LogInformation("Local backup file: {Path} ({Size:N0} bytes)", backupFilePath, fileSize);
            }
            else if (sshService != null && sshService.IsConfigured)
            {
                // Remote file - download via SSH
                progress?.Report(new BackupProgress("Downloading backup via SSH...", 22));
                logger.LogInformation("Backup file is on remote server, downloading via SSH...");

                var sshFileInfo = sshService.GetFileInfo(backupFilePath);
                if (sshFileInfo == null)
                {
                    logger.LogError("Backup file not found on remote server: {Path}", backupFilePath);
                    return false;
                }

                fileSize = sshFileInfo.Value.Size;
                logger.LogInformation("Remote backup file: {Path} ({Size:N0} bytes)", backupFilePath, fileSize);

                Directory.CreateDirectory(tempDownloadDir);
                needsCleanup = true;
                localBackupPath = Path.Combine(tempDownloadDir, Path.GetFileName(backupFilePath));

                var downloadProgress = new Progress<(long BytesDownloaded, long TotalBytes)>(p =>
                {
                    var percent = 22 + (int)(p.BytesDownloaded * 3.0 / p.TotalBytes); // 22-25%
                    progress?.Report(new BackupProgress(
                        $"SSH Download: {p.BytesDownloaded:N0} / {p.TotalBytes:N0} bytes",
                        percent));
                });

                var downloadSuccess = await sshService.DownloadFileAsync(
                    backupFilePath,
                    localBackupPath,
                    downloadProgress,
                    cancellationToken);

                if (!downloadSuccess)
                {
                    logger.LogError("Failed to download backup file via SSH");
                    if (needsCleanup && Directory.Exists(tempDownloadDir))
                        Directory.Delete(tempDownloadDir, true);
                    return false;
                }

                fileSize = new FileInfo(localBackupPath).Length;
            }
            else
            {
                logger.LogError("Backup file not found locally and SSH not configured: {Path}", backupFilePath);
                logger.LogError("Configure SSH access to download backup files from remote Proxmox server");
                return false;
            }

            var fileInfo = new FileInfo(localBackupPath);
            logger.LogInformation("Backup file ready: {Path} ({Size:N0} bytes)", localBackupPath, fileInfo.Length);

            // Create Yandex Disk folder
            progress?.Report(new BackupProgress("Creating Yandex Disk folder...", 25));
            var backupFolderName = $"{vm.Name}_{vmId}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            var remoteFolderPath = $"{yandexDiskFolder.TrimEnd('/')}/{backupFolderName}";

            if (!await yandexDiskService.CreateFolderAsync(remoteFolderPath, cancellationToken))
            {
                logger.LogError("Failed to create folder on Yandex Disk");
                return false;
            }

            // Prepare metadata
            var metadata = new BackupMetadata
            {
                OriginalFileName = fileInfo.Name,
                OriginalFileSize = fileInfo.Length,
                OriginalFileHash = "", // Will be computed
                TotalChunks = 0, // Will be set
                ChunkSize = FileSplitter.MaxChunkSize,
                CreatedAt = DateTime.UtcNow,
                ProxmoxVersion = proxmoxVersion,
                VmType = vm.Type,
                VmId = vmId,
                VmName = vm.Name,
                NodeName = node
            };

            // Check if file needs splitting
            var needsSplitting = FileSplitter.NeedsSplitting(fileInfo.Length);
            string tempDir = Path.Combine(Path.GetTempPath(), $"proxmox_backup_{Guid.NewGuid():N}");

            try
            {
                if (needsSplitting)
                {
                    logger.LogInformation("File exceeds 50GB limit, splitting into chunks...");
                    progress?.Report(new BackupProgress("Splitting file into chunks...", 30));

                    Directory.CreateDirectory(tempDir);

                    var splitProgress = new Progress<(long BytesProcessed, long TotalBytes)>(p =>
                    {
                        var percent = 30 + (int)(p.BytesProcessed * 30.0 / p.TotalBytes);
                        progress?.Report(new BackupProgress(
                            $"Splitting: {p.BytesProcessed:N0} / {p.TotalBytes:N0} bytes",
                            percent));
                    });

                    var (chunkPaths, updatedMetadata) = await fileSplitter.SplitFileAsync(
                        localBackupPath,
                        tempDir,
                        metadata,
                        splitProgress,
                        cancellationToken);

                    metadata = updatedMetadata;

                    // Upload chunks
                    progress?.Report(new BackupProgress("Uploading chunks to Yandex Disk...", 60));

                    for (var i = 0; i < chunkPaths.Count; i++)
                    {
                        var chunkPath = chunkPaths[i];
                        var chunkFileName = Path.GetFileName(chunkPath);
                        var remoteChunkPath = $"{remoteFolderPath}/{chunkFileName}";

                        var chunkProgress = new Progress<(long BytesUploaded, long TotalBytes)>(p =>
                        {
                            var chunkPercent = p.BytesUploaded * 100.0 / p.TotalBytes;
                            var overallPercent = 60 + (int)((i + chunkPercent / 100.0) * 35.0 / chunkPaths.Count);
                            progress?.Report(new BackupProgress(
                                $"Uploading chunk {i + 1}/{chunkPaths.Count}: {chunkPercent:F1}%",
                                overallPercent));
                        });

                        var uploadSuccess = await yandexDiskService.UploadFileAsync(
                            chunkPath,
                            remoteChunkPath,
                            progress: chunkProgress,
                            cancellationToken: cancellationToken);

                        if (!uploadSuccess)
                        {
                            logger.LogError("Failed to upload chunk {Index}", i);
                            return false;
                        }
                    }
                }
                else
                {
                    // Single file upload
                    progress?.Report(new BackupProgress("Computing file hash...", 30));
                    metadata = metadata with
                    {
                        OriginalFileHash = await fileHasher.ComputeHashAsync(localBackupPath, null, cancellationToken),
                        TotalChunks = 1,
                        Chunks =
                        [
                            new ChunkInfo
                            {
                                Index = 0,
                                FileName = fileInfo.Name,
                                Size = fileInfo.Length,
                                Hash = "", // Same as original
                                Offset = 0
                            }
                        ]
                    };
                    metadata.Chunks[0] = metadata.Chunks[0] with { Hash = metadata.OriginalFileHash };

                    progress?.Report(new BackupProgress("Uploading to Yandex Disk...", 40));
                    var remoteFilePath = $"{remoteFolderPath}/{fileInfo.Name}";

                    var uploadProgress = new Progress<(long BytesUploaded, long TotalBytes)>(p =>
                    {
                        var percent = 40 + (int)(p.BytesUploaded * 55.0 / p.TotalBytes);
                        progress?.Report(new BackupProgress(
                            $"Uploading: {p.BytesUploaded:N0} / {p.TotalBytes:N0} bytes",
                            percent));
                    });

                    var uploadSuccess = await yandexDiskService.UploadFileAsync(
                        localBackupPath,
                        remoteFilePath,
                        progress: uploadProgress,
                        cancellationToken: cancellationToken);

                    if (!uploadSuccess)
                    {
                        logger.LogError("Failed to upload backup file");
                        return false;
                    }
                }

                // Upload metadata
                progress?.Report(new BackupProgress("Uploading metadata...", 95));
                var metadataJson = JsonSerializer.Serialize(metadata, JsonOptions);
                var metadataPath = Path.Combine(tempDir, MetadataFileName);

                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                await File.WriteAllTextAsync(metadataPath, metadataJson, cancellationToken);

                var metadataUploadSuccess = await yandexDiskService.UploadFileAsync(
                    metadataPath,
                    $"{remoteFolderPath}/{MetadataFileName}",
                    cancellationToken: cancellationToken);

                if (!metadataUploadSuccess)
                {
                    logger.LogWarning("Failed to upload metadata file");
                }

                progress?.Report(new BackupProgress("Backup completed successfully!", 100));
                logger.LogInformation("Backup completed: {RemotePath}", remoteFolderPath);

                return true;
            }
            finally
            {
                // Cleanup temp directories
                foreach (var dir in new[] { tempDir, tempDownloadDir })
                {
                    if (Directory.Exists(dir))
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to cleanup temp directory: {TempDir}", dir);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup failed");
            return false;
        }
    }

    /// <summary>
    /// Uploads an existing backup file to Yandex Disk using streaming.
    /// Streams directly from source (SSH or local) to Yandex Disk without downloading to local disk.
    /// </summary>
    /// <param name="backupPath">Path to the backup file</param>
    /// <param name="isRemoteFile">True if file is on remote server (use SSH), false if local</param>
    public async Task<bool> UploadExistingBackupAsync(
        string backupPath,
        string yandexDiskFolder,
        int vmId,
        string vmName,
        string vmType,
        string nodeName,
        string proxmoxVersion,
        bool isRemoteFile = false,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Create stream source based on file location
        IStreamSource streamSource;
        long fileSize;
        string fileName = Path.GetFileName(backupPath);

        progress?.Report(new BackupProgress("Preparing file access...", 1));

        if (isRemoteFile)
        {
            // Remote file - use SSH streaming (no download!)
            if (sshService == null || !sshService.IsConfigured)
            {
                logger.LogError("SSH not configured for remote file access");
                return false;
            }

            var sshFileInfo = sshService.GetFileInfo(backupPath);
            if (sshFileInfo == null)
            {
                logger.LogError("Backup file not found on remote server: {Path}", backupPath);
                return false;
            }

            fileSize = sshFileInfo.Value.Size;
            streamSource = sshService.CreateStreamSource(backupPath);
            logger.LogInformation("Using SSH streaming for upload: {Path} ({Size:N0} bytes)", backupPath, fileSize);
        }
        else
        {
            // Local file - use local file streaming
            if (!File.Exists(backupPath))
            {
                logger.LogError("Backup file not found: {Path}", backupPath);
                return false;
            }

            streamSource = new LocalFileStreamSource(backupPath);
            fileSize = streamSource.TotalLength;
            logger.LogInformation("Using local file streaming for upload: {Path} ({Size:N0} bytes)", backupPath, fileSize);
        }

        // Create Yandex Disk folder
        progress?.Report(new BackupProgress("Creating Yandex Disk folder...", 2));
        var backupFolderName = $"{vmName}_{vmId}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        var remoteFolderPath = $"{yandexDiskFolder.TrimEnd('/')}/{backupFolderName}";

        if (!await yandexDiskService.CreateFolderAsync(remoteFolderPath, cancellationToken))
        {
            logger.LogError("Failed to create Yandex Disk folder: {Path}", remoteFolderPath);
            return false;
        }

        // Perform streaming upload (handles chunking automatically)
        var result = await UploadWithStreamingAsync(
            streamSource,
            fileName,
            fileSize,
            remoteFolderPath,
            vmName,
            vmType,
            vmId,
            nodeName,
            proxmoxVersion,
            progress,
            cancellationToken);

        if (result)
        {
            progress?.Report(new BackupProgress("Complete!", 100));
            logger.LogInformation("Streaming upload complete: {Path} -> {RemotePath}", backupPath, remoteFolderPath);
        }

        return result;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Resolves the actual backup file path using pvesm (local or SSH) with API fallback
    /// </summary>
    private async Task<string?> ResolveBackupPathAsync(string node, string storage, string volId)
    {
        // Try local pvesm first (if running on Proxmox server)
        foreach (var pvesmPath in new[] { "pvesm", "/usr/sbin/pvesm" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pvesmPath,
                    Arguments = $"path {volId}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        var path = output.Trim();
                        logger.LogInformation("Resolved path via local pvesm: {Path}", path);
                        return path;
                    }
                }
            }
            catch
            {
                // pvesm not available locally
            }
        }

        // Try pvesm via SSH
        if (sshService != null && sshService.IsConfigured)
        {
            var sshPath = sshService.GetProxmoxVolumePath(volId);
            if (!string.IsNullOrEmpty(sshPath))
            {
                logger.LogInformation("Resolved path via SSH pvesm: {Path}", sshPath);
                return sshPath;
            }
        }

        // Fallback to API
        var apiPath = await proxmoxService.GetBackupFilePathAsync(node, storage, volId);
        if (!string.IsNullOrEmpty(apiPath))
        {
            logger.LogInformation("Resolved path via API: {Path}", apiPath);
        }
        return apiPath;
    }

    /// <summary>
    /// Downloads and reassembles a backup from Yandex Disk
    /// </summary>
    public async Task<string?> DownloadAndReassembleAsync(
        string yandexDiskBackupFolder,
        string localOutputPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Download metadata
            progress?.Report(new BackupProgress("Downloading metadata...", 5));
            var metadataRemotePath = $"{yandexDiskBackupFolder}/{MetadataFileName}";
            var tempDir = Path.Combine(Path.GetTempPath(), $"proxmox_restore_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var metadataLocalPath = Path.Combine(tempDir, MetadataFileName);
            if (!await yandexDiskService.DownloadFileAsync(metadataRemotePath, metadataLocalPath, cancellationToken: cancellationToken))
            {
                logger.LogError("Failed to download metadata");
                return null;
            }

            var metadataJson = await File.ReadAllTextAsync(metadataLocalPath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<BackupMetadata>(metadataJson, JsonOptions);

            if (metadata == null)
            {
                logger.LogError("Failed to parse metadata");
                return null;
            }

            logger.LogInformation("Restoring backup: {FileName} ({Size:N0} bytes, {Chunks} chunks)",
                metadata.OriginalFileName, metadata.OriginalFileSize, metadata.TotalChunks);

            // Download chunks
            progress?.Report(new BackupProgress("Downloading chunks...", 10));

            for (var i = 0; i < metadata.Chunks.Count; i++)
            {
                var chunk = metadata.Chunks[i];
                var chunkLocalPath = Path.Combine(tempDir, chunk.FileName);
                var chunkRemotePath = $"{yandexDiskBackupFolder}/{chunk.FileName}";

                var downloadProgress = new Progress<(long BytesDownloaded, long TotalBytes)>(p =>
                {
                    var chunkPercent = p.BytesDownloaded * 100.0 / p.TotalBytes;
                    var overallPercent = 10 + (int)((i + chunkPercent / 100.0) * 60.0 / metadata.Chunks.Count);
                    progress?.Report(new BackupProgress(
                        $"Downloading chunk {i + 1}/{metadata.Chunks.Count}: {chunkPercent:F1}%",
                        overallPercent));
                });

                if (!await yandexDiskService.DownloadFileAsync(chunkLocalPath, chunkRemotePath, downloadProgress, cancellationToken))
                {
                    logger.LogError("Failed to download chunk {Index}", i);
                    return null;
                }
            }

            // Reassemble if needed
            if (metadata.TotalChunks > 1)
            {
                progress?.Report(new BackupProgress("Reassembling file...", 70));

                var reassembleProgress = new Progress<(long BytesProcessed, long TotalBytes)>(p =>
                {
                    var percent = 70 + (int)(p.BytesProcessed * 25.0 / p.TotalBytes);
                    progress?.Report(new BackupProgress(
                        $"Reassembling: {p.BytesProcessed:N0} / {p.TotalBytes:N0} bytes",
                        percent));
                });

                var outputPath = await fileSplitter.ReassembleFileAsync(
                    metadata,
                    tempDir,
                    localOutputPath,
                    reassembleProgress,
                    cancellationToken);

                progress?.Report(new BackupProgress("Restore completed!", 100));
                return outputPath;
            }
            else
            {
                // Single file - just verify and move
                progress?.Report(new BackupProgress("Verifying file integrity...", 90));
                var singleFilePath = Path.Combine(tempDir, metadata.Chunks[0].FileName);

                if (!await fileHasher.VerifyHashAsync(singleFilePath, metadata.OriginalFileHash, cancellationToken: cancellationToken))
                {
                    logger.LogError("File integrity check failed");
                    return null;
                }

                var outputPath = Path.Combine(localOutputPath, metadata.OriginalFileName);
                Directory.CreateDirectory(localOutputPath);
                File.Move(singleFilePath, outputPath, true);

                progress?.Report(new BackupProgress("Restore completed!", 100));
                return outputPath;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore failed");
            return null;
        }
    }

    /// <summary>
    /// Lists available backups on Yandex Disk
    /// </summary>
    public async Task<List<BackupMetadata>> ListBackupsAsync(
        string yandexDiskFolder,
        CancellationToken cancellationToken = default)
    {
        var backups = new List<BackupMetadata>();

        try
        {
            var folders = await yandexDiskService.ListFolderAsync(yandexDiskFolder, 1000, cancellationToken);

            foreach (var folder in folders.Where(f => f.Type == global::YandexDisk.Client.Protocol.ResourceType.Dir))
            {
                try
                {
                    var metadataPath = $"{folder.Path}/{MetadataFileName}";
                    var tempPath = Path.GetTempFileName();

                    try
                    {
                        if (await yandexDiskService.DownloadFileAsync(metadataPath, tempPath, cancellationToken: cancellationToken))
                        {
                            var json = await File.ReadAllTextAsync(tempPath, cancellationToken);
                            var metadata = JsonSerializer.Deserialize<BackupMetadata>(json, JsonOptions);
                            if (metadata != null)
                            {
                                backups.Add(metadata);
                            }
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not read metadata from folder {Path}", folder.Path);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list backups");
        }

        return backups;
    }

    /// <summary>
    /// Creates a backup and uploads it using streaming (no intermediate local files).
    /// Reads data directly from source (SSH or local) and uploads to Yandex Disk in a single pass.
    /// Computes SHA-256 hash in parallel with upload using TeeStream.
    /// </summary>
    public async Task<bool> StreamingBackupAndUploadAsync(
        string node,
        int vmId,
        string storage,
        string yandexDiskFolder,
        string? backupMode = "snapshot",
        string? compress = "zstd",
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get VM info
            progress?.Report(new BackupProgress("Getting VM information...", 0));
            var allVms = await proxmoxService.GetAllVirtualMachinesAsync(node);
            var vm = allVms.FirstOrDefault(v => v.VmId == vmId);

            if (vm == null)
            {
                logger.LogError("VM {VmId} not found on node {Node}", vmId, node);
                return false;
            }

            logger.LogInformation("Starting streaming backup for {VmType} {VmId} ({VmName})",
                vm.Type, vmId, vm.Name);

            // Get Proxmox version
            var proxmoxVersion = await proxmoxService.GetVersionAsync() ?? "unknown";

            // Create backup on Proxmox
            progress?.Report(new BackupProgress("Creating Proxmox backup...", 5));
            var upid = await proxmoxService.CreateBackupAsync(
                node, vmId, storage,
                mode: backupMode ?? "snapshot",
                compress: compress ?? "zstd");

            if (string.IsNullOrEmpty(upid))
            {
                logger.LogError("Failed to start backup task");
                return false;
            }

            // Wait for backup to complete
            progress?.Report(new BackupProgress("Waiting for backup to complete...", 10));
            var backupSuccess = await proxmoxService.WaitForTaskAsync(
                node, upid,
                progress: new Progress<string>(msg => progress?.Report(new BackupProgress(msg, 15))),
                cancellationToken: cancellationToken);

            if (!backupSuccess)
            {
                logger.LogError("Backup task failed");
                return false;
            }

            // Find the created backup
            progress?.Report(new BackupProgress("Finding backup file...", 20));
            var backups = await proxmoxService.GetBackupsAsync(node, storage, vmId);
            var latestBackup = backups
                .OrderByDescending(b => b.CTime)
                .FirstOrDefault();

            if (latestBackup == null)
            {
                logger.LogError("Could not find the created backup");
                return false;
            }

            // Get backup file path
            var backupFilePath = await ResolveBackupPathAsync(node, storage, latestBackup.VolId);
            if (string.IsNullOrEmpty(backupFilePath))
            {
                logger.LogError("Could not determine backup file path");
                return false;
            }

            // Create stream source
            IStreamSource streamSource;
            long fileSize;
            string fileName = Path.GetFileName(backupFilePath);

            if (File.Exists(backupFilePath))
            {
                // Local file access (same machine as Proxmox)
                streamSource = new LocalFileStreamSource(backupFilePath);
                fileSize = streamSource.TotalLength;
                logger.LogInformation("Using local file streaming: {Path} ({Size:N0} bytes)", backupFilePath, fileSize);
            }
            else if (sshService != null && sshService.IsConfigured)
            {
                // Remote file - use SSH streaming
                var sshFileInfo = sshService.GetFileInfo(backupFilePath);
                if (sshFileInfo == null)
                {
                    logger.LogError("Backup file not found on remote server: {Path}", backupFilePath);
                    return false;
                }

                fileSize = sshFileInfo.Value.Size;
                streamSource = sshService.CreateStreamSource(backupFilePath);
                logger.LogInformation("Using SSH streaming: {Path} ({Size:N0} bytes)", backupFilePath, fileSize);
            }
            else
            {
                logger.LogError("Backup file not found locally and SSH not configured: {Path}", backupFilePath);
                return false;
            }

            // Create Yandex Disk folder
            progress?.Report(new BackupProgress("Creating Yandex Disk folder...", 22));
            var backupFolderName = $"{vm.Name}_{vmId}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            var remoteFolderPath = $"{yandexDiskFolder.TrimEnd('/')}/{backupFolderName}";

            if (!await yandexDiskService.CreateFolderAsync(remoteFolderPath, cancellationToken))
            {
                logger.LogError("Failed to create folder on Yandex Disk");
                return false;
            }

            // Perform streaming upload
            var result = await UploadWithStreamingAsync(
                streamSource,
                fileName,
                fileSize,
                remoteFolderPath,
                vm.Name,
                vm.Type,
                vmId,
                node,
                proxmoxVersion,
                progress,
                cancellationToken);

            if (result)
            {
                progress?.Report(new BackupProgress("Streaming backup completed successfully!", 100));
                logger.LogInformation("Streaming backup completed: {RemotePath}", remoteFolderPath);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Streaming backup failed");
            return false;
        }
    }

    /// <summary>
    /// Internal method to perform streaming upload with chunking support.
    /// </summary>
    private async Task<bool> UploadWithStreamingAsync(
        IStreamSource source,
        string fileName,
        long fileSize,
        string remoteFolderPath,
        string vmName,
        string vmType,
        int vmId,
        string nodeName,
        string proxmoxVersion,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var needsSplitting = FileSplitter.NeedsSplitting(fileSize);

        // Calculate chunks
        var chunks = FileSplitter.CalculateChunks(fileName, fileSize);

        // Prepare metadata
        var metadata = new BackupMetadata
        {
            MetadataVersion = 2, // Streaming version
            OriginalFileName = fileName,
            OriginalFileSize = fileSize,
            OriginalFileHash = "", // Will be computed for single-chunk or aggregated
            TotalChunks = chunks.Count,
            ChunkSize = FileSplitter.MaxChunkSize,
            CreatedAt = DateTime.UtcNow,
            ProxmoxVersion = proxmoxVersion,
            VmType = vmType,
            VmId = vmId,
            VmName = vmName,
            NodeName = nodeName,
            Chunks = chunks
        };

        if (needsSplitting)
        {
            logger.LogInformation("File exceeds 50GB limit, streaming upload in {NumChunks} chunks...", chunks.Count);
            progress?.Report(new BackupProgress($"Streaming upload in {chunks.Count} chunks...", 5));

            // Upload each chunk with streaming (5-95% of progress bar)
            var allHashes = new List<string>();

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var remoteChunkPath = $"{remoteFolderPath}/{chunk.FileName}";

                var chunkProgress = new Progress<(long BytesUploaded, long TotalBytes)>(p =>
                {
                    var chunkPercent = p.BytesUploaded * 100.0 / p.TotalBytes;
                    var overallPercent = 5 + (int)((i + chunkPercent / 100.0) * 90.0 / chunks.Count); // 5-95%
                    progress?.Report(new BackupProgress(
                        $"Streaming chunk {i + 1}/{chunks.Count}: {chunkPercent:F1}%",
                        overallPercent));
                });

                var (success, hash) = await yandexDiskService.UploadStreamAsync(
                    source,
                    remoteChunkPath,
                    offset: chunk.Offset,
                    length: chunk.Size,
                    computeHash: true,
                    progress: chunkProgress,
                    cancellationToken: cancellationToken);

                if (!success)
                {
                    logger.LogError("Failed to stream upload chunk {Index}", i);
                    return false;
                }

                // Update chunk hash in metadata
                if (hash != null)
                {
                    metadata.Chunks[i] = chunk with { Hash = hash };
                    allHashes.Add(hash);
                }
            }

            // For multi-chunk uploads, we concatenate chunk hashes as original hash
            // (actual file hash would require reading all data again)
            metadata = metadata with { OriginalFileHash = string.Join(":", allHashes) };
        }
        else
        {
            // Single streaming upload (5-95% of progress bar)
            progress?.Report(new BackupProgress("Streaming upload...", 5));

            var uploadProgress = new Progress<(long BytesUploaded, long TotalBytes)>(p =>
            {
                var percent = 5 + (int)(p.BytesUploaded * 90.0 / p.TotalBytes); // 5-95%
                progress?.Report(new BackupProgress(
                    $"Streaming: {p.BytesUploaded:N0} / {p.TotalBytes:N0} bytes",
                    percent));
            });

            var remoteFilePath = $"{remoteFolderPath}/{fileName}";

            var (success, hash) = await yandexDiskService.UploadStreamAsync(
                source,
                remoteFilePath,
                computeHash: true,
                progress: uploadProgress,
                cancellationToken: cancellationToken);

            if (!success)
            {
                logger.LogError("Failed to stream upload backup file");
                return false;
            }

            // Update metadata with computed hash
            metadata = metadata with { OriginalFileHash = hash ?? "" };
            if (hash != null && metadata.Chunks.Count > 0)
            {
                metadata.Chunks[0] = metadata.Chunks[0] with { Hash = hash };
            }
        }

        // Upload metadata (95-100%)
        progress?.Report(new BackupProgress("Uploading metadata...", 95));

        var tempDir = Path.Combine(Path.GetTempPath(), $"proxmox_streaming_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var metadataJson = JsonSerializer.Serialize(metadata, JsonOptions);
            var metadataPath = Path.Combine(tempDir, MetadataFileName);
            await File.WriteAllTextAsync(metadataPath, metadataJson, cancellationToken);

            var metadataUploadSuccess = await yandexDiskService.UploadFileAsync(
                metadataPath,
                $"{remoteFolderPath}/{MetadataFileName}",
                cancellationToken: cancellationToken);

            if (!metadataUploadSuccess)
            {
                logger.LogWarning("Failed to upload metadata file");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { /* ignore */ }
            }
        }

        return true;
    }
}

/// <summary>
/// Progress information for backup/restore operations
/// </summary>
public sealed record BackupProgress(string Message, int PercentComplete);
