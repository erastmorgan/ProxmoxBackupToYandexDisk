using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.Models;

namespace ProxmoxBackupToYandexDisk.Core.YandexDisk;

/// <summary>
/// Secure storage for Yandex Disk OAuth tokens
/// </summary>
public sealed class YandexDiskTokenStorage(ILogger<YandexDiskTokenStorage> logger)
{
    private const string TokenFileName = "yandex_disk_token.enc";
    private const string DefaultConfigFolder = ".proxmox-backup";

    /// <summary>
    /// Saves token to encrypted file
    /// </summary>
    public async Task SaveTokenAsync(
        YandexDiskTokenResponse token,
        string? configPath = null,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetTokenFilePath(configPath);
        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tokenJson = JsonSerializer.Serialize(token);
        var encryptedData = ProtectData(tokenJson);

        await File.WriteAllBytesAsync(filePath, encryptedData, cancellationToken);
        logger.LogInformation("Token saved to {FilePath}", filePath);
    }

    /// <summary>
    /// Loads token from encrypted file
    /// </summary>
    public async Task<YandexDiskTokenResponse?> LoadTokenAsync(
        string? configPath = null,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetTokenFilePath(configPath);

        if (!File.Exists(filePath))
        {
            logger.LogDebug("Token file not found: {FilePath}", filePath);
            return null;
        }

        try
        {
            var encryptedData = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var tokenJson = UnprotectData(encryptedData);
            var token = JsonSerializer.Deserialize<YandexDiskTokenResponse>(tokenJson);

            logger.LogInformation("Token loaded from {FilePath}", filePath);
            return token;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load token from {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Deletes the saved token
    /// </summary>
    public void DeleteToken(string? configPath = null)
    {
        var filePath = GetTokenFilePath(configPath);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            logger.LogInformation("Token deleted: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Checks if a token exists
    /// </summary>
    public bool TokenExists(string? configPath = null)
    {
        var filePath = GetTokenFilePath(configPath);
        return File.Exists(filePath);
    }

    private static string GetTokenFilePath(string? configPath)
    {
        if (!string.IsNullOrEmpty(configPath))
            return Path.Combine(configPath, TokenFileName);

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, DefaultConfigFolder, TokenFileName);
    }

    private static byte[] ProtectData(string data)
    {
        var dataBytes = Encoding.UTF8.GetBytes(data);

        // Use DPAPI on Windows, or simple encryption with machine-specific key on other platforms
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Protect(dataBytes, null, DataProtectionScope.CurrentUser);
        }

        // For Linux/macOS, use a simple AES encryption with machine-specific key
        return EncryptWithMachineKey(dataBytes);
    }

    private static string UnprotectData(byte[] encryptedData)
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

        // Prepend IV to encrypted data
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

        // Extract IV from beginning of data
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
        // Get machine-specific identifier
        var machineId = Environment.MachineName + Environment.UserName;

        // On Linux, try to use machine-id
        if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-id"))
        {
            machineId = File.ReadAllText("/etc/machine-id").Trim() + Environment.UserName;
        }

        // Derive a 256-bit key using PBKDF2
        var salt = Encoding.UTF8.GetBytes("ProxmoxBackupToYandexDisk-Salt-v1");
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(machineId),
            salt,
            100000,
            HashAlgorithmName.SHA256,
            32);
    }
}
