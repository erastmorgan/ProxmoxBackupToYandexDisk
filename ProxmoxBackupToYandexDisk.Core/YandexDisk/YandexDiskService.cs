using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.Models;
using ProxmoxBackupToYandexDisk.Core.Streaming;
using YandexDisk.Client;
using YandexDisk.Client.Http;
using YandexDisk.Client.Protocol;

namespace ProxmoxBackupToYandexDisk.Core.YandexDisk;

/// <summary>
/// High-level service for Yandex Disk operations using the official SDK
/// </summary>
public sealed class YandexDiskService(
    ILogger<YandexDiskService> logger,
    YandexDiskOAuthService oAuthService,
    YandexDiskTokenStorage tokenStorage) : IDisposable
{
    private DiskHttpApi? _diskApi;
    private string? _accessToken;
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 2000;
    private const long MaxUploadSize = 49L * 1024 * 1024 * 1024; // 49 GB (leaving margin for 50 GB limit)

    /// <summary>
    /// Initializes the service with stored token or performs OAuth flow
    /// </summary>
    public async Task<bool> InitializeAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken = default)
    {
        // Try to load existing token
        var token = await tokenStorage.LoadTokenAsync(cancellationToken: cancellationToken);

        if (token != null && !string.IsNullOrEmpty(token.AccessToken))
        {
            logger.LogInformation("Found existing token, attempting to use it");
            _accessToken = token.AccessToken;
            _diskApi = new DiskHttpApi(_accessToken);

            // Verify token is still valid
            try
            {
                await _diskApi.MetaInfo.GetDiskInfoAsync(cancellationToken);
                logger.LogInformation("Existing token is valid");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Existing token is invalid, will try to refresh");

                // Try to refresh token
                if (!string.IsNullOrEmpty(token.RefreshToken))
                {
                    try
                    {
                        var newToken = await oAuthService.RefreshTokenAsync(
                            clientId, clientSecret, token.RefreshToken, cancellationToken);

                        await tokenStorage.SaveTokenAsync(newToken, cancellationToken: cancellationToken);
                        _accessToken = newToken.AccessToken;
                        _diskApi = new DiskHttpApi(_accessToken);
                        logger.LogInformation("Token refreshed successfully");
                        return true;
                    }
                    catch (Exception refreshEx)
                    {
                        logger.LogWarning(refreshEx, "Token refresh failed, will perform new authorization");
                    }
                }
            }
        }

        // Perform OAuth flow
        logger.LogInformation("Starting OAuth authorization flow");
        var newAuthToken = await oAuthService.AuthorizeAsync(clientId, clientSecret, cancellationToken: cancellationToken);
        await tokenStorage.SaveTokenAsync(newAuthToken, cancellationToken: cancellationToken);

        _accessToken = newAuthToken.AccessToken;
        _diskApi = new DiskHttpApi(_accessToken);

        return true;
    }

    /// <summary>
    /// Gets disk space information
    /// </summary>
    public async Task<(long TotalSpace, long UsedSpace, long FreeSpace)> GetDiskSpaceAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var diskInfo = await _diskApi!.MetaInfo.GetDiskInfoAsync(cancellationToken);
        var totalSpace = diskInfo.TotalSpace;
        var usedSpace = diskInfo.UsedSpace;
        var freeSpace = totalSpace - usedSpace;

        return (totalSpace, usedSpace, freeSpace);
    }

    /// <summary>
    /// Creates a folder, including parent folders if needed
    /// </summary>
    public async Task<bool> CreateFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        // Ensure path starts with /
        if (!path.StartsWith('/'))
            path = "/" + path;

        logger.LogInformation("Creating folder: {Path}", path);

        try
        {
            // Try to create the folder directly first
            await _diskApi!.Commands.CreateDictionaryAsync(path, cancellationToken);
            logger.LogInformation("Folder created: {Path}", path);
            return true;
        }
        catch (YandexApiException ex) when (ex.Message.Contains("DiskPathPointsToExistentDirectoryError") ||
                                           ex.Message.Contains("existent directory"))
        {
            logger.LogDebug("Folder already exists: {Path}", path);
            return true;
        }
        catch (YandexApiException ex) when (ex.Message.Contains("DiskPathDoesntExistsError"))
        {
            // Parent doesn't exist, create parent folders recursively
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && parent != "/")
            {
                await CreateFolderAsync(parent, cancellationToken);
                return await CreateFolderAsync(path, cancellationToken);
            }
            throw;
        }
    }

    /// <summary>
    /// Uploads a file to Yandex Disk with progress reporting
    /// </summary>
    public async Task<bool> UploadFileAsync(
        string localPath,
        string remotePath,
        bool overwrite = true,
        IProgress<(long BytesUploaded, long TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var fileInfo = new FileInfo(localPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Local file not found", localPath);

        if (fileInfo.Length > MaxUploadSize)
            throw new InvalidOperationException(
                $"File size ({fileInfo.Length:N0} bytes) exceeds maximum upload size ({MaxUploadSize:N0} bytes). " +
                "Use FileSplitter to split the file first.");

        // Ensure path starts with /
        if (!remotePath.StartsWith('/'))
            remotePath = "/" + remotePath;

        logger.LogInformation("Uploading {LocalPath} to {RemotePath} ({Size:N0} bytes)",
            localPath, remotePath, fileInfo.Length);

        // Ensure parent folder exists
        var parentPath = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parentPath) && parentPath != "/")
        {
            await CreateFolderAsync(parentPath, cancellationToken);
        }

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Get upload link
                var uploadLink = await _diskApi!.Files.GetUploadLinkAsync(
                    remotePath,
                    overwrite,
                    cancellationToken);

                // Upload with progress
                await using var fileStream = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var progressStream = new Streaming.ProgressReportingStream(fileStream, fileInfo.Length, progress);

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(24) };
                using var content = new StreamContent(progressStream);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                var response = await httpClient.PutAsync(uploadLink.Href, content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("File uploaded successfully: {RemotePath}", remotePath);
                    return true;
                }

                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Upload attempt {Attempt} failed: {StatusCode} - {Error}",
                    attempt, response.StatusCode, error);
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                logger.LogWarning(ex, "Upload attempt {Attempt} failed, retrying...", attempt);
            }

            if (attempt < MaxRetries)
                await Task.Delay(RetryDelayMs * attempt, cancellationToken);
        }

        logger.LogError("Failed to upload file after {MaxRetries} attempts", MaxRetries);
        return false;
    }

    /// <summary>
    /// Downloads a file from Yandex Disk
    /// </summary>
    public async Task<bool> DownloadFileAsync(
        string remotePath,
        string localPath,
        IProgress<(long BytesDownloaded, long TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (!remotePath.StartsWith('/'))
            remotePath = "/" + remotePath;

        logger.LogInformation("Downloading {RemotePath} to {LocalPath}", remotePath, localPath);

        try
        {
            // Get resource info for size
            var resource = await _diskApi!.MetaInfo.GetInfoAsync(
                new ResourceRequest { Path = remotePath },
                cancellationToken);

            var totalBytes = resource.Size;

            // Get download link
            var downloadLink = await _diskApi.Files.GetDownloadLinkAsync(remotePath, cancellationToken);

            // Download with progress
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(24) };
            using var response = await httpClient.GetAsync(
                downloadLink.Href,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Download failed: {StatusCode}", response.StatusCode);
                return false;
            }

            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(
                localPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);

            var buffer = new byte[81920];
            long bytesDownloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesDownloaded += bytesRead;
                progress?.Report((bytesDownloaded, totalBytes));
            }

            logger.LogInformation("File downloaded: {LocalPath}", localPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download file {RemotePath}", remotePath);
            return false;
        }
    }

    /// <summary>
    /// Lists contents of a folder
    /// </summary>
    public async Task<List<Resource>> ListFolderAsync(
        string path,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (!path.StartsWith('/'))
            path = "/" + path;

        try
        {
            var resource = await _diskApi!.MetaInfo.GetInfoAsync(
                new ResourceRequest { Path = path, Limit = limit },
                cancellationToken);

            return resource.Embedded?.Items?.ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list folder {Path}", path);
            return [];
        }
    }

    /// <summary>
    /// Gets information about a resource
    /// </summary>
    public async Task<Resource?> GetResourceInfoAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (!path.StartsWith('/'))
            path = "/" + path;

        try
        {
            return await _diskApi!.MetaInfo.GetInfoAsync(
                new ResourceRequest { Path = path },
                cancellationToken);
        }
        catch (YandexApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a resource
    /// </summary>
    public async Task<bool> DeleteAsync(
        string path,
        bool permanently = false,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (!path.StartsWith('/'))
            path = "/" + path;

        try
        {
            await _diskApi!.Commands.DeleteAsync(
                new DeleteFileRequest { Path = path, Permanently = permanently },
                cancellationToken);

            logger.LogInformation("Deleted: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Checks if a resource exists
    /// </summary>
    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var resource = await GetResourceInfoAsync(path, cancellationToken);
        return resource != null;
    }

    /// <summary>
    /// Uploads data from a stream source to Yandex Disk with streaming (no intermediate files).
    /// Computes SHA-256 hash in parallel with upload using TeeStream.
    /// </summary>
    /// <param name="source">The stream source providing the data</param>
    /// <param name="remotePath">Remote path on Yandex Disk</param>
    /// <param name="offset">Starting offset in the source (for chunked uploads)</param>
    /// <param name="length">Length to upload (null for entire source from offset)</param>
    /// <param name="computeHash">Whether to compute SHA-256 hash during upload</param>
    /// <param name="overwrite">Whether to overwrite existing file</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of (success, computed hash or null)</returns>
    public async Task<(bool Success, string? Hash)> UploadStreamAsync(
        IStreamSource source,
        string remotePath,
        long offset = 0,
        long? length = null,
        bool computeHash = true,
        bool overwrite = true,
        IProgress<(long BytesUploaded, long TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var uploadLength = length ?? (source.TotalLength - offset);

        if (uploadLength > MaxUploadSize)
            throw new InvalidOperationException(
                $"Upload size ({uploadLength:N0} bytes) exceeds maximum upload size ({MaxUploadSize:N0} bytes). " +
                "Use chunked streaming upload.");

        // Ensure path starts with /
        if (!remotePath.StartsWith('/'))
            remotePath = "/" + remotePath;

        logger.LogInformation("Streaming upload to {RemotePath} ({Size:N0} bytes, offset: {Offset})",
            remotePath, uploadLength, offset);

        // Ensure parent folder exists
        var parentPath = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parentPath) && parentPath != "/")
        {
            await CreateFolderAsync(parentPath, cancellationToken);
        }

        HashingStreamConsumer? hashConsumer = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Get upload link
                var uploadLink = await _diskApi!.Files.GetUploadLinkAsync(
                    remotePath,
                    overwrite,
                    cancellationToken);

                // Create retryable stream from source
                var retryableStream = new RetryableUploadStream(source, offset, uploadLength, cancellationToken);

                // Wrap with hash consumer if requested
                Stream uploadStream;
                if (computeHash)
                {
                    hashConsumer?.Dispose();
                    hashConsumer = new HashingStreamConsumer();
                    var teeStream = new TeeStream(retryableStream, hashConsumer, leaveOpen: false);
                    uploadStream = new Streaming.ProgressReportingStream(teeStream, uploadLength, progress, leaveOpen: false);
                }
                else
                {
                    uploadStream = new Streaming.ProgressReportingStream(retryableStream, uploadLength, progress, leaveOpen: false);
                }

                await using (uploadStream)
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(24) };
                    using var content = new StreamContent(uploadStream);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = uploadLength;

                    var response = await httpClient.PutAsync(uploadLink.Href, content, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        // Ensure hash is finalized
                        if (computeHash && hashConsumer != null && !hashConsumer.IsCompleted)
                        {
                            hashConsumer.Complete();
                        }

                        logger.LogInformation("Streaming upload completed: {RemotePath}", remotePath);
                        return (true, hashConsumer?.Hash);
                    }

                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    logger.LogWarning("Streaming upload attempt {Attempt} failed: {StatusCode} - {Error}",
                        attempt, response.StatusCode, error);
                }
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                logger.LogWarning(ex, "Streaming upload attempt {Attempt} failed, retrying...", attempt);
            }

            if (attempt < MaxRetries)
                await Task.Delay(RetryDelayMs * attempt, cancellationToken);
        }

        hashConsumer?.Dispose();
        logger.LogError("Failed to stream upload after {MaxRetries} attempts", MaxRetries);
        return (false, null);
    }

    private void EnsureInitialized()
    {
        if (_diskApi == null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");
    }

    public void Dispose()
    {
        _diskApi?.Dispose();
    }
}
