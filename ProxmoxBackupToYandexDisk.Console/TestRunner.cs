using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.FileOperations;
using ProxmoxBackupToYandexDisk.Core.Models;
using ProxmoxBackupToYandexDisk.Core.Proxmox;
using ProxmoxBackupToYandexDisk.Core.Services;
using ProxmoxBackupToYandexDisk.Core.YandexDisk;
using Spectre.Console;

namespace ProxmoxBackupToYandexDisk.Console;

public static class TestRunner
{
    public static async Task<int> RunTestsAsync(string vmName)
    {
        System.Console.WriteLine("=== ProxmoxBackupToYandexDisk Test Runner ===\n");

        // Build config host
        var configBuilder = Host.CreateApplicationBuilder();
        configBuilder.Logging.SetMinimumLevel(LogLevel.Information);
        configBuilder.Services.AddSingleton<AppConfigService>();
        var configHost = configBuilder.Build();
        var configService = configHost.Services.GetRequiredService<AppConfigService>();

        // Load configuration
        var config = await configService.LoadConfigAsync();
        if (config == null)
        {
            System.Console.WriteLine("ERROR: No saved configuration found. Run the app normally first to set up config.");
            return 1;
        }

        System.Console.WriteLine($"Loaded config: Proxmox={config.Proxmox.Host}:{config.Proxmox.Port}, Yandex folder={config.Yandex.BackupFolder}\n");

        // Build main host
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddHttpClient<YandexDiskOAuthService>();
        builder.Services.AddSingleton<FileHasher>();
        builder.Services.AddSingleton<FileSplitter>();
        builder.Services.AddSingleton<YandexDiskTokenStorage>();
        builder.Services.AddSingleton<YandexDiskOAuthService>();
        builder.Services.AddSingleton<YandexDiskService>();
        builder.Services.AddSingleton<AppConfigService>();
        builder.Services.AddSingleton<SshDownloadService>();

        var host = builder.Build();

        // Configure SSH service
        var sshService = host.Services.GetRequiredService<SshDownloadService>();

        // Check if SSH password is configured, if not prompt for it
        if (string.IsNullOrEmpty(config.Proxmox.SshPassword))
        {
            System.Console.WriteLine("\nSSH access is needed to download backup files from remote Proxmox server.");
            System.Console.WriteLine($"SSH will connect to {config.Proxmox.Host} as {config.Proxmox.SshUsername}");

            // Try using the Proxmox password first
            if (!config.Proxmox.UseApiToken && !string.IsNullOrEmpty(config.Proxmox.Password))
            {
                System.Console.WriteLine("Using Proxmox password for SSH...");
                config.Proxmox.SshPassword = config.Proxmox.Password;
            }
            else
            {
                System.Console.Write("Enter SSH password: ");
                config.Proxmox.SshPassword = ReadPassword();
                System.Console.WriteLine();
            }

            // Save updated config
            await configService.SaveConfigAsync(config);
        }

        sshService.Configure(
            config.Proxmox.Host,
            config.Proxmox.SshUsername,
            config.Proxmox.SshPassword!,
            config.Proxmox.SshPort);

        var results = new List<(string Test, bool Success, string Message)>();

        // Test 1: Proxmox Connection
        System.Console.WriteLine("--- Test 1: Proxmox Connection ---");
        var proxmoxLogger = host.Services.GetRequiredService<ILogger<ProxmoxService>>();
        var proxmoxService = new ProxmoxService(proxmoxLogger, config.Proxmox.Host, config.Proxmox.Port);

        try
        {
            bool connected;
            if (config.Proxmox.UseApiToken)
            {
                proxmoxService.LoginWithApiToken(config.Proxmox.ApiTokenId!, config.Proxmox.ApiTokenSecret!);
                connected = true;
            }
            else
            {
                connected = await proxmoxService.LoginAsync(config.Proxmox.Username, config.Proxmox.Password, config.Proxmox.Realm);
            }

            if (connected)
            {
                results.Add(("Proxmox Connection", true, "Connected successfully"));
                System.Console.WriteLine("PASS: Connected to Proxmox\n");
            }
            else
            {
                results.Add(("Proxmox Connection", false, "Authentication failed"));
                System.Console.WriteLine("FAIL: Authentication failed\n");
                return 1;
            }
        }
        catch (Exception ex)
        {
            results.Add(("Proxmox Connection", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
            return 1;
        }

        // Test 2: Get Proxmox Version
        System.Console.WriteLine("--- Test 2: Get Proxmox Version ---");
        try
        {
            var version = await proxmoxService.GetVersionAsync();
            if (version != null)
            {
                results.Add(("Get Version", true, version));
                System.Console.WriteLine($"PASS: Proxmox VE {version}\n");
            }
            else
            {
                results.Add(("Get Version", false, "Version is null"));
                System.Console.WriteLine("FAIL: Version is null\n");
            }
        }
        catch (Exception ex)
        {
            results.Add(("Get Version", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        // Test 3: List Nodes
        System.Console.WriteLine("--- Test 3: List Nodes ---");
        List<string> nodes = [];
        try
        {
            nodes = await proxmoxService.GetNodesAsync();
            if (nodes.Count > 0)
            {
                results.Add(("List Nodes", true, $"Found {nodes.Count} node(s): {string.Join(", ", nodes)}"));
                System.Console.WriteLine($"PASS: Found {nodes.Count} node(s): {string.Join(", ", nodes)}\n");
            }
            else
            {
                results.Add(("List Nodes", false, "No nodes found"));
                System.Console.WriteLine("FAIL: No nodes found\n");
            }
        }
        catch (Exception ex)
        {
            results.Add(("List Nodes", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        if (nodes.Count == 0) return 1;

        var selectedNode = nodes[0];

        // Test 4: List VMs
        System.Console.WriteLine("--- Test 4: List QEMU VMs ---");
        List<ProxmoxVm> vms = [];
        try
        {
            vms = await proxmoxService.GetVmsAsync(selectedNode);
            results.Add(("List QEMU VMs", true, $"Found {vms.Count} VM(s)"));
            System.Console.WriteLine($"PASS: Found {vms.Count} QEMU VM(s)");
            foreach (var vm in vms)
            {
                System.Console.WriteLine($"  - {vm.VmId}: {vm.Name} ({vm.Status})");
            }
            System.Console.WriteLine();
        }
        catch (Exception ex)
        {
            results.Add(("List QEMU VMs", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        // Test 5: List Containers
        System.Console.WriteLine("--- Test 5: List LXC Containers ---");
        List<ProxmoxVm> containers = [];
        try
        {
            containers = await proxmoxService.GetContainersAsync(selectedNode);
            results.Add(("List LXC Containers", true, $"Found {containers.Count} container(s)"));
            System.Console.WriteLine($"PASS: Found {containers.Count} LXC container(s)");
            foreach (var ct in containers)
            {
                System.Console.WriteLine($"  - {ct.VmId}: {ct.Name} ({ct.Status})");
            }
            System.Console.WriteLine();
        }
        catch (Exception ex)
        {
            results.Add(("List LXC Containers", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        // Test 6: List Backup Storages
        System.Console.WriteLine("--- Test 6: List Backup Storages ---");
        List<ProxmoxStorage> storages = [];
        try
        {
            storages = await proxmoxService.GetBackupStoragesAsync(selectedNode);
            results.Add(("List Backup Storages", true, $"Found {storages.Count} storage(s)"));
            System.Console.WriteLine($"PASS: Found {storages.Count} backup storage(s)");
            foreach (var s in storages)
            {
                System.Console.WriteLine($"  - {s.Storage}: {s.Type}, {FormatSize(s.Avail)} free");
            }
            System.Console.WriteLine();
        }
        catch (Exception ex)
        {
            results.Add(("List Backup Storages", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        // Test 7: List Existing Backups on Proxmox
        System.Console.WriteLine("--- Test 7: List Existing Backups on Proxmox ---");
        try
        {
            foreach (var storage in storages)
            {
                var backups = await proxmoxService.GetBackupsAsync(selectedNode, storage.Storage);
                System.Console.WriteLine($"Storage '{storage.Storage}': {backups.Count} backup(s)");
                foreach (var b in backups.Take(5))
                {
                    System.Console.WriteLine($"  - {b.VolId} ({FormatSize(b.Size)})");
                }
                if (backups.Count > 5)
                    System.Console.WriteLine($"  ... and {backups.Count - 5} more");
            }
            results.Add(("List Proxmox Backups", true, "Listed backups from all storages"));
            System.Console.WriteLine("PASS\n");
        }
        catch (Exception ex)
        {
            results.Add(("List Proxmox Backups", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        // Test 8: Connect to Yandex Disk
        System.Console.WriteLine("--- Test 8: Yandex Disk Connection ---");
        var yandexDiskService = host.Services.GetRequiredService<YandexDiskService>();
        try
        {
            var yandexConnected = await yandexDiskService.InitializeAsync(config.Yandex.ClientId, config.Yandex.ClientSecret);
            if (yandexConnected)
            {
                var (total, used, free) = await yandexDiskService.GetDiskSpaceAsync();
                results.Add(("Yandex Disk Connection", true, $"{FormatSize(used)} used / {FormatSize(total)} total"));
                System.Console.WriteLine($"PASS: Connected to Yandex Disk ({FormatSize(used)} used / {FormatSize(total)} total)\n");
            }
            else
            {
                results.Add(("Yandex Disk Connection", false, "Failed to connect"));
                System.Console.WriteLine("FAIL: Failed to connect\n");
                return 1;
            }
        }
        catch (Exception ex)
        {
            results.Add(("Yandex Disk Connection", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
            return 1;
        }

        // Test 9: Create/Check Backup Folder on Yandex Disk
        System.Console.WriteLine("--- Test 9: Create Backup Folder on Yandex Disk ---");
        try
        {
            var created = await yandexDiskService.CreateFolderAsync(config.Yandex.BackupFolder);
            results.Add(("Create Yandex Folder", true, config.Yandex.BackupFolder));
            System.Console.WriteLine($"PASS: Folder '{config.Yandex.BackupFolder}' ready\n");
        }
        catch (Exception ex)
        {
            results.Add(("Create Yandex Folder", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        // Test SSH Connection
        System.Console.WriteLine("--- Test: SSH Connection ---");
        try
        {
            var sshConnected = await sshService.TestConnectionAsync();
            if (sshConnected)
            {
                results.Add(("SSH Connection", true, $"Connected to {config.Proxmox.Host}:{config.Proxmox.SshPort}"));
                System.Console.WriteLine($"PASS: SSH connected to {config.Proxmox.Host}\n");
            }
            else
            {
                results.Add(("SSH Connection", false, "Connection failed"));
                System.Console.WriteLine("FAIL: SSH connection failed\n");
            }
        }
        catch (Exception ex)
        {
            results.Add(("SSH Connection", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        // Test 10: List Backups on Yandex Disk
        System.Console.WriteLine("--- Test 10: List Backups on Yandex Disk ---");
        var backupService = new BackupService(
            host.Services.GetRequiredService<ILogger<BackupService>>(),
            proxmoxService,
            yandexDiskService,
            host.Services.GetRequiredService<FileSplitter>(),
            host.Services.GetRequiredService<FileHasher>(),
            sshService);

        try
        {
            var yandexBackups = await backupService.ListBackupsAsync(config.Yandex.BackupFolder);
            results.Add(("List Yandex Backups", true, $"Found {yandexBackups.Count} backup(s)"));
            System.Console.WriteLine($"PASS: Found {yandexBackups.Count} backup(s) on Yandex Disk");
            foreach (var b in yandexBackups)
            {
                System.Console.WriteLine($"  - {b.VmName} ({b.VmId}): {FormatSize(b.OriginalFileSize)}, {b.CreatedAt:yyyy-MM-dd HH:mm}");
            }
            System.Console.WriteLine();
        }
        catch (Exception ex)
        {
            results.Add(("List Yandex Backups", false, ex.Message));
            System.Console.WriteLine($"FAIL: {ex.Message}\n");
        }

        // Find target VM
        var allVms = vms.Concat(containers).ToList();
        var targetVm = allVms.FirstOrDefault(v => v.Name.Contains(vmName, StringComparison.OrdinalIgnoreCase));

        if (targetVm == null)
        {
            System.Console.WriteLine($"WARNING: VM '{vmName}' not found. Available VMs:");
            foreach (var v in allVms)
            {
                System.Console.WriteLine($"  - {v.VmId}: {v.Name} ({v.Type})");
            }
            System.Console.WriteLine();
            results.Add(("Find Target VM", false, $"VM '{vmName}' not found"));
        }
        else
        {
            results.Add(("Find Target VM", true, $"Found {targetVm.Name} (ID: {targetVm.VmId}, Type: {targetVm.Type})"));
            System.Console.WriteLine($"Found target VM: {targetVm.Name} (ID: {targetVm.VmId}, Type: {targetVm.Type})\n");

            // Check for existing backups on Proxmox
            var existingBackups = new List<ProxmoxBackupInfo>();
            foreach (var storage in storages)
            {
                var backups = await proxmoxService.GetBackupsAsync(selectedNode, storage.Storage, targetVm.VmId);
                existingBackups.AddRange(backups);
            }

            // Test 11: Upload Existing Backup or Create New
            if (storages.Count > 0)
            {
                // Check if there's enough space for a new backup
                var availableSpace = storages[0].Avail;
                var hasExistingBackup = existingBackups.Count > 0;
                var latestExisting = existingBackups.OrderByDescending(b => b.CTime).FirstOrDefault();
                var estimatedBackupSize = latestExisting?.Size ?? 30L * 1024 * 1024 * 1024; // 30GB default estimate

                if (hasExistingBackup && availableSpace < estimatedBackupSize)
                {
                    // Not enough space - use existing backup for upload test
                    System.Console.WriteLine($"--- Test 11: Upload Existing Backup for {targetVm.Name} ---");
                    System.Console.WriteLine($"Storage has only {FormatSize(availableSpace)} free, using existing backup.");
                    System.Console.WriteLine($"Existing backup: {latestExisting!.VolId} ({FormatSize(latestExisting.Size)})");
                    System.Console.WriteLine();

                    try
                    {
                        // Get backup file path
                        var backupFilePath = await proxmoxService.GetBackupFilePathAsync(
                            selectedNode, storages[0].Storage, latestExisting.VolId);

                        if (string.IsNullOrEmpty(backupFilePath))
                        {
                            results.Add(("Upload Existing Backup", false, "Could not determine backup file path"));
                            System.Console.WriteLine("FAIL: Could not determine backup file path\n");
                        }
                        else
                        {
                            System.Console.WriteLine($"Backup path: {backupFilePath}");

                            // Download via SSH and upload to Yandex
                            var tempDir = Path.Combine(Path.GetTempPath(), $"proxmox_test_{Guid.NewGuid():N}");
                            Directory.CreateDirectory(tempDir);

                            try
                            {
                                var localPath = Path.Combine(tempDir, Path.GetFileName(backupFilePath));

                                System.Console.WriteLine("Downloading backup via SSH...");
                                var downloadProgress = new Progress<(long BytesDownloaded, long TotalBytes)>(p =>
                                {
                                    var percent = p.BytesDownloaded * 100.0 / p.TotalBytes;
                                    System.Console.Write($"\r  SSH Download: {percent:F1}% ({FormatSize(p.BytesDownloaded)} / {FormatSize(p.TotalBytes)})    ");
                                });

                                var downloadSuccess = await sshService.DownloadFileAsync(
                                    backupFilePath,
                                    localPath,
                                    downloadProgress);

                                System.Console.WriteLine();

                                if (!downloadSuccess)
                                {
                                    results.Add(("Upload Existing Backup", false, "SSH download failed"));
                                    System.Console.WriteLine("FAIL: SSH download failed\n");
                                }
                                else
                                {
                                    System.Console.WriteLine($"Downloaded to: {localPath}");
                                    var version = await proxmoxService.GetVersionAsync() ?? "unknown";

                                    var uploadProgress = new Progress<BackupProgress>(p =>
                                    {
                                        System.Console.WriteLine($"  [{p.PercentComplete:F1}%] {p.Message}");
                                    });

                                    var uploadSuccess = await backupService.UploadExistingBackupAsync(
                                        localPath,
                                        config.Yandex.BackupFolder,
                                        targetVm.VmId,
                                        targetVm.Name,
                                        targetVm.Type,
                                        selectedNode,
                                        version,
                                        isRemoteFile: false, // Tests use local files
                                        uploadProgress);

                                    if (uploadSuccess)
                                    {
                                        results.Add(("Upload Existing Backup", true, $"Backup of {targetVm.Name} uploaded"));
                                        System.Console.WriteLine("PASS: Existing backup uploaded successfully\n");
                                    }
                                    else
                                    {
                                        results.Add(("Upload Existing Backup", false, "Upload failed"));
                                        System.Console.WriteLine("FAIL: Upload failed\n");
                                    }
                                }
                            }
                            finally
                            {
                                if (Directory.Exists(tempDir))
                                    Directory.Delete(tempDir, true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(("Upload Existing Backup", false, ex.Message));
                        System.Console.WriteLine($"FAIL: {ex.Message}\n");
                    }
                }
                else
                {
                    // Create new backup with STREAMING method
                    System.Console.WriteLine($"--- Test 11: STREAMING Backup for {targetVm.Name} ---");
                    System.Console.WriteLine($"Node: {selectedNode}");
                    System.Console.WriteLine($"VM ID: {targetVm.VmId}");
                    System.Console.WriteLine($"Storage: {storages[0].Storage}");
                    System.Console.WriteLine($"Mode: snapshot");
                    System.Console.WriteLine($"Compression: zstd");
                    System.Console.WriteLine("[STREAMING: No intermediate files, direct upload]");
                    System.Console.WriteLine();

                    try
                    {
                        var progress = new Progress<BackupProgress>(p =>
                        {
                            System.Console.WriteLine($"  [{p.PercentComplete:F1}%] {p.Message}");
                        });

                        // Use new streaming method!
                        var success = await backupService.StreamingBackupAndUploadAsync(
                            selectedNode,
                            targetVm.VmId,
                            storages[0].Storage,
                            config.Yandex.BackupFolder,
                            "snapshot",
                            "zstd",
                            progress);

                        if (success)
                        {
                            results.Add(("Streaming Backup & Upload", true, $"Streaming backup of {targetVm.Name} completed"));
                            System.Console.WriteLine("PASS: Streaming backup created and uploaded successfully\n");
                        }
                        else
                        {
                            results.Add(("Streaming Backup & Upload", false, "Streaming backup failed"));
                            System.Console.WriteLine("FAIL: Streaming backup failed\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(("Streaming Backup & Upload", false, ex.Message));
                        System.Console.WriteLine($"FAIL: {ex.Message}\n");
                    }
                }

                // Test 12: Verify Backup on Yandex Disk
                System.Console.WriteLine("--- Test 12: Verify Backup on Yandex Disk ---");
                try
                {
                    var yandexBackupsAfter = await backupService.ListBackupsAsync(config.Yandex.BackupFolder);
                    var newBackup = yandexBackupsAfter
                        .Where(b => b.VmId == targetVm.VmId)
                        .OrderByDescending(b => b.CreatedAt)
                        .FirstOrDefault();

                    if (newBackup != null)
                    {
                        results.Add(("Verify Yandex Backup", true, $"Found backup from {newBackup.CreatedAt:yyyy-MM-dd HH:mm}"));
                        System.Console.WriteLine($"PASS: Backup verified on Yandex Disk");
                        System.Console.WriteLine($"  - Metadata version: {newBackup.MetadataVersion}");
                        System.Console.WriteLine($"  - Original size: {FormatSize(newBackup.OriginalFileSize)}");
                        System.Console.WriteLine($"  - Chunks: {newBackup.TotalChunks}");
                        var hashDisplay = newBackup.OriginalFileHash.Length > 16
                            ? $"{newBackup.OriginalFileHash[..16]}..."
                            : newBackup.OriginalFileHash;
                        System.Console.WriteLine($"  - Hash: {hashDisplay}");
                        System.Console.WriteLine();
                    }
                    else
                    {
                        results.Add(("Verify Yandex Backup", false, "Backup not found on Yandex Disk"));
                        System.Console.WriteLine("FAIL: Backup not found on Yandex Disk\n");
                    }
                }
                catch (Exception ex)
                {
                    results.Add(("Verify Yandex Backup", false, ex.Message));
                    System.Console.WriteLine($"FAIL: {ex.Message}\n");
                }
            }
        }

        // Print Summary
        System.Console.WriteLine("\n=== TEST SUMMARY ===\n");
        var passed = results.Count(r => r.Success);
        var failed = results.Count(r => !r.Success);

        foreach (var (test, success, message) in results)
        {
            var status = success ? "PASS" : "FAIL";
            var color = success ? "\u001b[32m" : "\u001b[31m";
            System.Console.WriteLine($"{color}[{status}]\u001b[0m {test}: {message}");
        }

        System.Console.WriteLine($"\n{passed} passed, {failed} failed out of {results.Count} tests");

        return failed > 0 ? 1 : 0;
    }

    private static string FormatSize(long bytes)
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

    private static string ReadPassword()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = System.Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                System.Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                System.Console.Write("*");
            }
        }
        return password.ToString();
    }
}
