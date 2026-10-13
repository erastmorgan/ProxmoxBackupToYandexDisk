using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProxmoxBackupToYandexDisk.Core.Services;

/// <summary>
/// Application configuration with secure storage
/// </summary>
public sealed class AppConfig
{
    public ProxmoxConfig Proxmox { get; set; } = new();
    public YandexConfig Yandex { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();
    public SchedulerConfig Scheduler { get; set; } = new();
}

public sealed class ProxmoxConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 8006;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Realm { get; set; } = "pam";
    public string? ApiTokenId { get; set; }
    public string? ApiTokenSecret { get; set; }
    public bool UseApiToken { get; set; }

    // SSH settings for remote backup file access
    public int SshPort { get; set; } = 22;
    public string SshUsername { get; set; } = "root";
    public string? SshPassword { get; set; }
}

public sealed class YandexConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string BackupFolder { get; set; } = "/ProxmoxBackups";
}

public sealed class TelegramConfig
{
    public string? BotToken { get; set; }
    public string? ChatId { get; set; }
    public bool NotifyOnSuccess { get; set; } = false;
    public bool NotifyOnError { get; set; } = true;
}

public sealed class SchedulerConfig
{
    public bool Enabled { get; set; }
    public List<ScheduledBackupJob> Jobs { get; set; } = [];
}

public sealed class ScheduledBackupJob
{
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty; // e.g., "0 2 * * *" for 2 AM daily
    public string NodeName { get; set; } = string.Empty;
    public List<int> VmIds { get; set; } = [];
    public string Storage { get; set; } = string.Empty;
    public string BackupMode { get; set; } = "snapshot";
    public string Compression { get; set; } = "zstd";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Service for managing application configuration with encryption
/// </summary>
public sealed class AppConfigService(ILogger<AppConfigService> logger)
{
    private const string ConfigFileName = "app_config.enc";
    private const string DefaultConfigFolder = ".proxmox-backup";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Saves configuration to encrypted file
    /// </summary>
    public async Task SaveConfigAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var filePath = GetConfigFilePath();
        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var configJson = JsonSerializer.Serialize(config, JsonOptions);
        var encryptedData = EncryptData(configJson);

        await File.WriteAllBytesAsync(filePath, encryptedData, cancellationToken);
        logger.LogInformation("Configuration saved to {FilePath}", filePath);
    }

    /// <summary>
    /// Loads configuration from encrypted file
    /// </summary>
    public async Task<AppConfig?> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        var filePath = GetConfigFilePath();

        if (!File.Exists(filePath))
        {
            logger.LogDebug("Config file not found: {FilePath}", filePath);
            return null;
        }

        try
        {
            var encryptedData = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var configJson = DecryptData(encryptedData);
            var config = JsonSerializer.Deserialize<AppConfig>(configJson, JsonOptions);

            logger.LogInformation("Configuration loaded from {FilePath}", filePath);
            return config;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load config from {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Deletes the configuration file
    /// </summary>
    public void DeleteConfig()
    {
        var filePath = GetConfigFilePath();

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            logger.LogInformation("Configuration deleted: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Checks if configuration exists
    /// </summary>
    public bool ConfigExists()
    {
        return File.Exists(GetConfigFilePath());
    }

    private static string GetConfigFilePath()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, DefaultConfigFolder, ConfigFileName);
    }

    private static byte[] EncryptData(string data)
    {
        var dataBytes = Encoding.UTF8.GetBytes(data);

        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Protect(dataBytes, null, DataProtectionScope.CurrentUser);
        }

        return EncryptWithMachineKey(dataBytes);
    }

    private static string DecryptData(byte[] encryptedData)
    {
        byte[] decryptedData;

        if (OperatingSystem.IsWindows())
        {
            decryptedData = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
        }
        else
        {
            decryptedData = DecryptWithMachineKey(encryptedData);
        }

        return Encoding.UTF8.GetString(decryptedData);
    }

    private static byte[] EncryptWithMachineKey(byte[] data)
    {
        using var aes = Aes.Create();
        var key = DeriveKeyFromMachineId();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);

        var result = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

        return result;
    }

    private static byte[] DecryptWithMachineKey(byte[] encryptedData)
    {
        using var aes = Aes.Create();
        var key = DeriveKeyFromMachineId();
        aes.Key = key;

        var iv = new byte[aes.BlockSize / 8];
        Buffer.BlockCopy(encryptedData, 0, iv, 0, iv.Length);
        aes.IV = iv;

        var cipherText = new byte[encryptedData.Length - iv.Length];
        Buffer.BlockCopy(encryptedData, iv.Length, cipherText, 0, cipherText.Length);

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
    }

    private static byte[] DeriveKeyFromMachineId()
    {
        var machineId = Environment.MachineName + Environment.UserName;

        if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-id"))
        {
            machineId = File.ReadAllText("/etc/machine-id").Trim() + Environment.UserName;
        }

        var salt = Encoding.UTF8.GetBytes("ProxmoxBackupToYandexDisk-Config-Salt-v1");
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(machineId),
            salt,
            100000,
            HashAlgorithmName.SHA256,
            32);
    }
}
