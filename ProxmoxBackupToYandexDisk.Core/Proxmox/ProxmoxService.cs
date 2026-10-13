using Corsinvest.ProxmoxVE.Api;
using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.Models;

namespace ProxmoxBackupToYandexDisk.Core.Proxmox;

/// <summary>
/// High-level service for Proxmox VE operations using Corsinvest SDK
/// Supports Proxmox VE 6.0 and newer
/// </summary>
public sealed class ProxmoxService : IDisposable
{
    private readonly ILogger<ProxmoxService> _logger;
    private readonly PveClient _client;
    private bool _isAuthenticated;

    public ProxmoxService(
        ILogger<ProxmoxService> logger,
        string host,
        int port = 8006,
        bool validateCertificate = false)
    {
        _logger = logger;
        _client = new PveClient(host, port)
        {
            ValidateCertificate = validateCertificate
        };
        _logger.LogDebug("Proxmox client created for {Host}:{Port}, SSL validation: {Validate}",
            host, port, validateCertificate);
    }

    /// <summary>
    /// Authenticates with username and password
    /// </summary>
    public async Task<bool> LoginAsync(
        string username,
        string password,
        string realm = "pam")
    {
        _logger.LogInformation("Authenticating to Proxmox as {Username}@{Realm}", username, realm);

        try
        {
            // Corsinvest SDK: LoginAsync(userName, password, realm, otp)
            var result = await _client.LoginAsync($"{username}@{realm}", password);
            _isAuthenticated = result;

            if (result)
            {
                _logger.LogInformation("Successfully authenticated to Proxmox");
            }
            else
            {
                _logger.LogError("Authentication failed - check username/password");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication error: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Authenticates with API token
    /// </summary>
    public void LoginWithApiToken(string tokenId, string tokenSecret)
    {
        _logger.LogInformation("Configuring API token authentication: {TokenId}", tokenId);
        _client.ApiToken = $"{tokenId}={tokenSecret}";
        _isAuthenticated = true;
    }

    /// <summary>
    /// Gets Proxmox version
    /// </summary>
    public async Task<string?> GetVersionAsync()
    {
        EnsureAuthenticated();

        try
        {
            var result = await _client.Version.Version();
            if (result.IsSuccessStatusCode)
            {
                var data = result.Response.data;
                return $"{data.version}-{data.release}";
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get version");
            return null;
        }
    }

    /// <summary>
    /// Gets list of cluster nodes
    /// </summary>
    public async Task<List<string>> GetNodesAsync()
    {
        EnsureAuthenticated();

        try
        {
            var result = await _client.Nodes.Index();
            if (!result.IsSuccessStatusCode)
                return [];

            var nodes = new List<string>();
            foreach (var node in result.Response.data)
            {
                nodes.Add((string)node.node);
            }
            return nodes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get nodes");
            return [];
        }
    }

    /// <summary>
    /// Gets all QEMU VMs on a node
    /// </summary>
    public async Task<List<ProxmoxVm>> GetVmsAsync(string node)
    {
        EnsureAuthenticated();

        try
        {
            var result = await _client.Nodes[node].Qemu.Vmlist();
            if (!result.IsSuccessStatusCode)
                return [];

            var vms = new List<ProxmoxVm>();
            foreach (var vm in result.Response.data)
            {
                vms.Add(new ProxmoxVm
                {
                    VmId = (int)vm.vmid,
                    Name = (string)(vm.name ?? $"VM {vm.vmid}"),
                    Status = (string)(vm.status ?? "unknown"),
                    Type = "qemu",
                    Mem = (long)(vm.mem ?? 0),
                    MaxMem = (long)(vm.maxmem ?? 0),
                    Cpu = (double)(vm.cpu ?? 0),
                    MaxCpu = (int)(vm.cpus ?? 1),
                    Disk = (long)(vm.disk ?? 0),
                    MaxDisk = (long)(vm.maxdisk ?? 0)
                });
            }
            return vms;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get VMs for node {Node}", node);
            return [];
        }
    }

    /// <summary>
    /// Gets all LXC containers on a node
    /// </summary>
    public async Task<List<ProxmoxVm>> GetContainersAsync(string node)
    {
        EnsureAuthenticated();

        try
        {
            var result = await _client.Nodes[node].Lxc.Vmlist();
            if (!result.IsSuccessStatusCode)
                return [];

            var containers = new List<ProxmoxVm>();
            foreach (var ct in result.Response.data)
            {
                containers.Add(new ProxmoxVm
                {
                    VmId = (int)ct.vmid,
                    Name = (string)(ct.name ?? $"CT {ct.vmid}"),
                    Status = (string)(ct.status ?? "unknown"),
                    Type = "lxc",
                    Mem = (long)(ct.mem ?? 0),
                    MaxMem = (long)(ct.maxmem ?? 0),
                    Cpu = (double)(ct.cpu ?? 0),
                    MaxCpu = (int)(ct.cpus ?? 1),
                    Disk = (long)(ct.disk ?? 0),
                    MaxDisk = (long)(ct.maxdisk ?? 0)
                });
            }
            return containers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get containers for node {Node}", node);
            return [];
        }
    }

    /// <summary>
    /// Gets all VMs and containers on a node
    /// </summary>
    public async Task<List<ProxmoxVm>> GetAllVirtualMachinesAsync(string node)
    {
        var vms = await GetVmsAsync(node);
        var containers = await GetContainersAsync(node);
        return [.. vms, .. containers];
    }

    /// <summary>
    /// Gets all storages on a node (for debugging)
    /// </summary>
    public async Task<List<ProxmoxStorage>> GetAllStoragesAsync(string node)
    {
        EnsureAuthenticated();

        try
        {
            var result = await _client.Nodes[node].Storage.Index();
            if (!result.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get all storages: {Status}", result.ReasonPhrase);
                return [];
            }

            var storages = new List<ProxmoxStorage>();
            foreach (var storage in result.Response.data)
            {
                var dict = (IDictionary<string, object>)storage;
                storages.Add(new ProxmoxStorage
                {
                    Storage = GetDynamicValue<string>(dict, "storage", ""),
                    Type = GetDynamicValue<string>(dict, "type", "unknown"),
                    Content = GetDynamicValue<string>(dict, "content", ""),
                    Path = GetDynamicValue<string>(dict, "path", ""),
                    Total = GetDynamicValue<long>(dict, "total", 0),
                    Used = GetDynamicValue<long>(dict, "used", 0),
                    Avail = GetDynamicValue<long>(dict, "avail", 0),
                    Enabled = GetDynamicValue<int>(dict, "enabled", 1),
                    Active = GetDynamicValue<int>(dict, "active", 1)
                });
            }
            return storages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all storages for node {Node}", node);
            return [];
        }
    }

    /// <summary>
    /// Gets storages that support backups
    /// </summary>
    public async Task<List<ProxmoxStorage>> GetBackupStoragesAsync(string node)
    {
        EnsureAuthenticated();

        try
        {
            // First, get all storages to see what's available
            var allResult = await _client.Nodes[node].Storage.Index();
            if (allResult.IsSuccessStatusCode)
            {
                _logger.LogDebug("All storages on node {Node}:", node);
                foreach (var s in allResult.Response.data)
                {
                    var d = (IDictionary<string, object>)s;
                    var name = GetDynamicValue<string>(d, "storage", "");
                    var content = GetDynamicValue<string>(d, "content", "");
                    var enabled = GetDynamicValue<int>(d, "enabled", 1);
                    var active = GetDynamicValue<int>(d, "active", 1);
                    _logger.LogDebug("  Storage: {Name}, Content: {Content}, Enabled: {Enabled}, Active: {Active}",
                        name, content, enabled, active);
                }
            }

            // Now get only backup-capable storages
            var result = await _client.Nodes[node].Storage.Index("backup");
            if (!result.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get backup storages: {Status}", result.ReasonPhrase);
                return [];
            }

            var storages = new List<ProxmoxStorage>();
            foreach (var storage in result.Response.data)
            {
                var dict = (IDictionary<string, object>)storage;
                var storageName = GetDynamicValue<string>(dict, "storage", "");
                var content = GetDynamicValue<string>(dict, "content", "");
                var enabled = GetDynamicValue<int>(dict, "enabled", 1);
                var active = GetDynamicValue<int>(dict, "active", 1);

                _logger.LogDebug("Backup storage found: {Name}, Content: {Content}, Enabled: {Enabled}, Active: {Active}",
                    storageName, content, enabled, active);

                // Only add active and enabled storages
                if (enabled == 1 && active == 1)
                {
                    storages.Add(new ProxmoxStorage
                    {
                        Storage = storageName,
                        Type = GetDynamicValue<string>(dict, "type", "unknown"),
                        Content = content,
                        Path = GetDynamicValue<string>(dict, "path", ""),
                        Total = GetDynamicValue<long>(dict, "total", 0),
                        Used = GetDynamicValue<long>(dict, "used", 0),
                        Avail = GetDynamicValue<long>(dict, "avail", 0),
                        Enabled = enabled,
                        Active = active
                    });
                }
                else
                {
                    _logger.LogInformation("Storage {Name} skipped: enabled={Enabled}, active={Active}",
                        storageName, enabled, active);
                }
            }

            _logger.LogInformation("Found {Count} backup-capable storage(s) on node {Node}", storages.Count, node);
            return storages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get storages for node {Node}", node);
            return [];
        }
    }

    /// <summary>
    /// Gets list of backups for a VM
    /// </summary>
    public async Task<List<ProxmoxBackupInfo>> GetBackupsAsync(string node, string storage, int? vmId = null)
    {
        EnsureAuthenticated();

        try
        {
            var result = await _client.Nodes[node].Storage[storage].Content.Index("backup", vmId);
            if (!result.IsSuccessStatusCode)
                return [];

            var backups = new List<ProxmoxBackupInfo>();
            foreach (var backup in result.Response.data)
            {
                backups.Add(new ProxmoxBackupInfo
                {
                    VolId = (string)backup.volid,
                    Format = (string)(backup.format ?? "unknown"),
                    Size = (long)(backup.size ?? 0),
                    CTime = (long)(backup.ctime ?? 0),
                    VmId = (int)(backup.vmid ?? 0),
                    Notes = (string)(backup.notes ?? "")
                });
            }
            return backups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get backups");
            return [];
        }
    }

    /// <summary>
    /// Creates a backup of a VM or container
    /// </summary>
    public async Task<string?> CreateBackupAsync(
        string node,
        int vmId,
        string storage,
        string mode = "snapshot",
        string compress = "zstd",
        string? notes = null)
    {
        EnsureAuthenticated();

        _logger.LogInformation(
            "Creating backup for VM {VmId} on node {Node} to storage {Storage}",
            vmId, node, storage);

        try
        {
            var result = await _client.Nodes[node].Vzdump.Vzdump(
                storage: storage,
                vmid: vmId.ToString(),
                mode: mode,
                compress: compress,
                notes_template: notes ?? "Backup created by ProxmoxBackupToYandexDisk"
            );

            if (!result.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to create backup: {Error}", result.ReasonPhrase);
                return null;
            }

            var upid = (string)result.Response.data;
            _logger.LogInformation("Backup task started: {Upid}", upid);
            return upid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup");
            return null;
        }
    }

    /// <summary>
    /// Gets task log lines
    /// </summary>
    public async Task<List<string>> GetTaskLogAsync(string node, string upid, int start = 0, int limit = 50)
    {
        EnsureAuthenticated();

        try
        {
            var result = await _client.Nodes[node].Tasks[upid].Log.ReadTaskLog(start: start, limit: limit);
            if (!result.IsSuccessStatusCode)
                return [];

            var lines = new List<string>();
            foreach (var line in result.Response.data)
            {
                var dict = (IDictionary<string, object>)line;
                var text = GetDynamicValue<string>(dict, "t", "");
                if (!string.IsNullOrEmpty(text))
                    lines.Add(text);
            }
            return lines;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get task log");
            return [];
        }
    }

    /// <summary>
    /// Waits for a task to complete with detailed logging
    /// </summary>
    public async Task<bool> WaitForTaskAsync(
        string node,
        string upid,
        TimeSpan? timeout = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        timeout ??= TimeSpan.FromHours(24);
        var startTime = DateTime.UtcNow;
        var lastLogLine = 0;

        _logger.LogInformation("Waiting for task: {Upid}", upid);

        while (DateTime.UtcNow - startTime < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await _client.Nodes[node].Tasks[upid].Status.ReadTaskStatus();
                if (!result.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to get task status: {Status}", result.ReasonPhrase);
                    await Task.Delay(5000, cancellationToken);
                    continue;
                }

                var status = (string)result.Response.data.status;

                // Get and display new log lines
                var logLines = await GetTaskLogAsync(node, upid, lastLogLine, 100);
                foreach (var line in logLines)
                {
                    _logger.LogInformation("[Proxmox] {LogLine}", line);
                    progress?.Report(line);
                    lastLogLine++;
                }

                if (status == "stopped")
                {
                    // Get any remaining log lines
                    var finalLogLines = await GetTaskLogAsync(node, upid, lastLogLine, 1000);
                    foreach (var line in finalLogLines)
                    {
                        _logger.LogInformation("[Proxmox] {LogLine}", line);
                        progress?.Report(line);
                    }

                    var exitStatus = (string)(result.Response.data.exitstatus ?? "OK");
                    var success = exitStatus == "OK";

                    if (success)
                    {
                        _logger.LogInformation("Task completed successfully");
                    }
                    else
                    {
                        _logger.LogError("Task failed with exit status: {ExitStatus}", exitStatus);

                        // Try to get more detailed error info from the last log lines
                        var errorLogLines = await GetTaskLogAsync(node, upid, 0, 1000);
                        if (errorLogLines.Count > 0)
                        {
                            _logger.LogError("=== Full task log ===");
                            foreach (var line in errorLogLines)
                            {
                                _logger.LogError("[Proxmox Log] {LogLine}", line);
                            }
                            _logger.LogError("=== End of task log ===");
                        }
                    }
                    return success;
                }

                progress?.Report($"Task status: {status}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking task status");
            }

            await Task.Delay(3000, cancellationToken);
        }

        _logger.LogError("Task timed out after {Timeout}", timeout);
        return false;
    }

    /// <summary>
    /// Gets the file path of a backup on the storage
    /// </summary>
    public async Task<string?> GetBackupFilePathAsync(string node, string storage, string volId)
    {
        EnsureAuthenticated();

        try
        {
            _logger.LogDebug("Getting backup file path for storage={Storage}, volId={VolId}", storage, volId);

            // Get storage info to find the path
            var storageResult = await _client.Nodes[node].Storage[storage].Status.ReadStatus();
            if (!storageResult.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get storage status for {Storage}", storage);
                return null;
            }

            var dict = (IDictionary<string, object>)storageResult.Response.data;
            var storagePath = GetDynamicValue(dict, "path", "/var/lib/vz");

            _logger.LogInformation("Storage {Storage} path: {Path}", storage, storagePath);

            // Log all keys in the response for debugging
            _logger.LogDebug("Storage {Storage} status keys: {Keys}", storage, string.Join(", ", dict.Keys));

            // Parse volid to get filename (format: storage:backup/vzdump-qemu-100-2024_01_01-12_00_00.vma.zst)
            var parts = volId.Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries);
            var fileName = parts.LastOrDefault() ?? volId;

            var fullPath = Path.Combine(storagePath, "dump", fileName);
            _logger.LogInformation("Resolved backup file path: {Path}", fullPath);

            return fullPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get backup file path for storage={Storage}, volId={VolId}", storage, volId);
            return null;
        }
    }

    /// <summary>
    /// Note: Proxmox API doesn't support direct backup file downloads.
    /// Use GetBackupFilePathAsync and access the file directly for local servers,
    /// or use SSH/SCP for remote access.
    /// </summary>
    public string? GetBackupDownloadNote()
    {
        return "Proxmox API doesn't support direct backup file downloads. " +
               "Use GetBackupFilePathAsync for local access or SSH/SCP for remote servers.";
    }

    private void EnsureAuthenticated()
    {
        if (!_isAuthenticated)
            throw new InvalidOperationException("Not authenticated. Call Login or LoginWithApiToken first.");
    }

    private static T GetDynamicValue<T>(IDictionary<string, object> dict, string key, T defaultValue)
    {
        if (dict.TryGetValue(key, out var value) && value != null)
        {
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }

    public void Dispose()
    {
        // PveClient doesn't implement IDisposable but we keep this for future compatibility
    }
}
