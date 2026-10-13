using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.Streaming;
using Renci.SshNet;

namespace ProxmoxBackupToYandexDisk.Core.Services;

/// <summary>
/// Service for downloading files from remote servers via SSH/SCP
/// </summary>
public sealed class SshDownloadService : IDisposable
{
    private readonly ILogger<SshDownloadService> _logger;
    private SftpClient? _sftpClient;
    private string? _host;
    private string? _username;
    private string? _password;
    private int _port = 22;

    public SshDownloadService(ILogger<SshDownloadService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Configures the SSH connection
    /// </summary>
    public void Configure(string host, string username, string password, int port = 22)
    {
        _host = host;
        _username = username;
        _password = password;
        _port = port;

        _logger.LogDebug("SSH configured for {Host}:{Port} as {Username}", host, port, username);
    }

    /// <summary>
    /// Checks if SSH is configured
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_host) && !string.IsNullOrEmpty(_username);

    /// <summary>
    /// Tests the SSH connection
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("SSH not configured");
            return false;
        }

        try
        {
            using var client = new SshClient(_host!, _port, _username!, _password!);
            await Task.Run(() => client.Connect(), cancellationToken);

            if (client.IsConnected)
            {
                _logger.LogInformation("SSH connection test successful");
                client.Disconnect();
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Executes a command on the remote server and returns the output
    /// </summary>
    public string? ExecuteCommand(string command)
    {
        if (!IsConfigured) return null;

        try
        {
            using var client = new SshClient(_host!, _port, _username!, _password!);
            client.Connect();

            if (!client.IsConnected)
                return null;

            var cmd = client.RunCommand(command);
            var result = cmd.Result?.Trim();

            client.Disconnect();

            _logger.LogDebug("SSH command '{Command}' returned: {Result}", command, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH command failed: {Command}", command);
            return null;
        }
    }

    /// <summary>
    /// Gets the actual file path for a Proxmox volume ID using pvesm
    /// </summary>
    public string? GetProxmoxVolumePath(string volId)
    {
        var result = ExecuteCommand($"pvesm path '{volId}'");
        if (!string.IsNullOrEmpty(result) && result.StartsWith("/"))
        {
            _logger.LogInformation("Resolved volume {VolId} to path: {Path}", volId, result);
            return result;
        }
        return null;
    }

    /// <summary>
    /// Downloads a file from the remote server
    /// </summary>
    public async Task<bool> DownloadFileAsync(
        string remotePath,
        string localPath,
        IProgress<(long BytesDownloaded, long TotalBytes)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogError("SSH not configured");
            return false;
        }

        try
        {
            EnsureConnected();

            // Get file size
            var fileInfo = _sftpClient!.Get(remotePath);
            var totalBytes = fileInfo.Length;

            _logger.LogInformation("Downloading {RemotePath} ({Size:N0} bytes)", remotePath, totalBytes);

            // Ensure local directory exists
            var localDir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
            {
                Directory.CreateDirectory(localDir);
            }

            // Download with progress
            await using var localStream = File.Create(localPath);
            await using var remoteStream = _sftpClient.OpenRead(remotePath);

            var buffer = new byte[1024 * 1024]; // 1 MB buffer
            long bytesDownloaded = 0;
            int bytesRead;

            while ((bytesRead = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesDownloaded += bytesRead;
                progress?.Report((bytesDownloaded, totalBytes));
            }

            _logger.LogInformation("Downloaded {LocalPath}", localPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download {RemotePath}", remotePath);
            return false;
        }
    }

    /// <summary>
    /// Checks if a file exists on the remote server
    /// </summary>
    public bool FileExists(string remotePath)
    {
        if (!IsConfigured) return false;

        try
        {
            EnsureConnected();
            return _sftpClient!.Exists(remotePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets file information from the remote server
    /// </summary>
    public (long Size, DateTime ModifiedTime)? GetFileInfo(string remotePath)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("GetFileInfo: SSH not configured");
            return null;
        }

        try
        {
            EnsureConnected();
            var attrs = _sftpClient!.GetAttributes(remotePath);
            _logger.LogDebug("GetFileInfo: Found file {Path}, Size={Size}", remotePath, attrs.Size);
            return (attrs.Size, attrs.LastWriteTime);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            _logger.LogWarning("GetFileInfo: File not found on remote server: {Path}", remotePath);
            return null;
        }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException ex)
        {
            _logger.LogError(ex, "GetFileInfo: Permission denied for {Path}", remotePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileInfo: Failed to get file info for {Path}", remotePath);
            return null;
        }
    }

    /// <summary>
    /// Gets detailed error information when trying to access a file
    /// </summary>
    public (bool Success, string? Error) TryGetFileInfo(string remotePath, out (long Size, DateTime ModifiedTime)? fileInfo)
    {
        fileInfo = null;

        if (!IsConfigured)
        {
            return (false, "SSH not configured");
        }

        try
        {
            EnsureConnected();
            var attrs = _sftpClient!.GetAttributes(remotePath);
            fileInfo = (attrs.Size, attrs.LastWriteTime);
            return (true, null);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return (false, $"File not found on remote server: {remotePath}");
        }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException)
        {
            return (false, $"Permission denied accessing: {remotePath}");
        }
        catch (Renci.SshNet.Common.SshConnectionException ex)
        {
            return (false, $"SSH connection error: {ex.Message}");
        }
        catch (Renci.SshNet.Common.SshAuthenticationException ex)
        {
            return (false, $"SSH authentication failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"SSH error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a stream source for streaming file content from the remote server.
    /// This allows streaming uploads without downloading the entire file first.
    /// </summary>
    /// <param name="remotePath">Path to the file on the remote server</param>
    /// <returns>An IStreamSource that can provide streams for the remote file</returns>
    /// <exception cref="InvalidOperationException">If SSH is not configured</exception>
    public IStreamSource CreateStreamSource(string remotePath)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("SSH not configured");

        return new SshStreamSource(
            _host!,
            _port,
            _username!,
            _password!,
            remotePath,
            _logger);
    }

    private void EnsureConnected()
    {
        if (_sftpClient == null || !_sftpClient.IsConnected)
        {
            _sftpClient?.Dispose();
            _sftpClient = new SftpClient(_host!, _port, _username!, _password!);
            _sftpClient.Connect();
            _logger.LogDebug("SFTP connected to {Host}", _host);
        }
    }

    public void Dispose()
    {
        _sftpClient?.Dispose();
    }
}
