using Microsoft.Extensions.Logging;
using Moq;
using ProxmoxBackupToYandexDisk.Core.Services;

namespace ProxmoxBackupToYandexDisk.Tests.Services;

public class AppConfigServiceTests : IDisposable
{
    private readonly Mock<ILogger<AppConfigService>> _loggerMock;
    private readonly AppConfigService _service;
    private readonly string _testConfigDir;

    public AppConfigServiceTests()
    {
        _loggerMock = new Mock<ILogger<AppConfigService>>();
        _testConfigDir = Path.Combine(Path.GetTempPath(), $"proxmox_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testConfigDir);

        // Set environment to use test directory
        Environment.SetEnvironmentVariable("HOME", _testConfigDir);
        _service = new AppConfigService(_loggerMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testConfigDir))
        {
            try { Directory.Delete(_testConfigDir, true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    [Fact]
    public async Task SaveConfigAsync_And_LoadConfigAsync_RoundTrip_Success()
    {
        // Arrange
        var config = new AppConfig
        {
            Proxmox = new ProxmoxConfig
            {
                Host = "192.168.1.100",
                Port = 8006,
                Username = "root",
                Password = "testpassword",
                Realm = "pam"
            },
            Yandex = new YandexConfig
            {
                ClientId = "test-client-id",
                ClientSecret = "test-client-secret",
                BackupFolder = "/TestBackups"
            }
        };

        // Act
        await _service.SaveConfigAsync(config);
        var loadedConfig = await _service.LoadConfigAsync();

        // Assert
        Assert.NotNull(loadedConfig);
        Assert.Equal(config.Proxmox.Host, loadedConfig.Proxmox.Host);
        Assert.Equal(config.Proxmox.Port, loadedConfig.Proxmox.Port);
        Assert.Equal(config.Proxmox.Username, loadedConfig.Proxmox.Username);
        Assert.Equal(config.Proxmox.Password, loadedConfig.Proxmox.Password);
        Assert.Equal(config.Yandex.ClientId, loadedConfig.Yandex.ClientId);
        Assert.Equal(config.Yandex.BackupFolder, loadedConfig.Yandex.BackupFolder);
    }

    [Fact]
    public async Task LoadConfigAsync_NoConfigFile_ReturnsNull()
    {
        // Act
        var config = await _service.LoadConfigAsync();

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public void ConfigExists_NoConfig_ReturnsFalse()
    {
        // Act
        var exists = _service.ConfigExists();

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task ConfigExists_AfterSave_ReturnsTrue()
    {
        // Arrange
        var config = new AppConfig();

        // Act
        await _service.SaveConfigAsync(config);
        var exists = _service.ConfigExists();

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task DeleteConfig_RemovesConfigFile()
    {
        // Arrange
        var config = new AppConfig();
        await _service.SaveConfigAsync(config);
        Assert.True(_service.ConfigExists());

        // Act
        _service.DeleteConfig();

        // Assert
        Assert.False(_service.ConfigExists());
    }

    [Fact]
    public async Task SaveConfigAsync_WithSchedulerConfig_PreservesJobs()
    {
        // Arrange
        var config = new AppConfig
        {
            Scheduler = new SchedulerConfig
            {
                Enabled = true,
                Jobs =
                [
                    new ScheduledBackupJob
                    {
                        Name = "Daily Backup",
                        CronExpression = "0 2 * * *",
                        NodeName = "pve",
                        VmIds = [100, 101, 102],
                        Storage = "local",
                        BackupMode = "snapshot",
                        Enabled = true
                    }
                ]
            }
        };

        // Act
        await _service.SaveConfigAsync(config);
        var loaded = await _service.LoadConfigAsync();

        // Assert
        Assert.NotNull(loaded);
        Assert.True(loaded.Scheduler.Enabled);
        Assert.Single(loaded.Scheduler.Jobs);
        Assert.Equal("Daily Backup", loaded.Scheduler.Jobs[0].Name);
        Assert.Equal("0 2 * * *", loaded.Scheduler.Jobs[0].CronExpression);
        Assert.Equal(3, loaded.Scheduler.Jobs[0].VmIds.Count);
    }

    [Fact]
    public async Task SaveConfigAsync_WithTelegramConfig_PreservesSettings()
    {
        // Arrange
        var config = new AppConfig
        {
            Telegram = new TelegramConfig
            {
                BotToken = "123456:ABC-DEF",
                ChatId = "-1001234567890",
                NotifyOnSuccess = true,
                NotifyOnError = true
            }
        };

        // Act
        await _service.SaveConfigAsync(config);
        var loaded = await _service.LoadConfigAsync();

        // Assert
        Assert.NotNull(loaded);
        Assert.True(loaded.Telegram.NotifyOnSuccess);
        Assert.True(loaded.Telegram.NotifyOnError);
        Assert.Equal("123456:ABC-DEF", loaded.Telegram.BotToken);
        Assert.Equal("-1001234567890", loaded.Telegram.ChatId);
    }

    [Fact]
    public async Task SaveConfigAsync_WithApiToken_PreservesTokenSettings()
    {
        // Arrange
        var config = new AppConfig
        {
            Proxmox = new ProxmoxConfig
            {
                Host = "proxmox.local",
                UseApiToken = true,
                ApiTokenId = "root@pam!backup",
                ApiTokenSecret = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
            }
        };

        // Act
        await _service.SaveConfigAsync(config);
        var loaded = await _service.LoadConfigAsync();

        // Assert
        Assert.NotNull(loaded);
        Assert.True(loaded.Proxmox.UseApiToken);
        Assert.Equal("root@pam!backup", loaded.Proxmox.ApiTokenId);
        Assert.Equal("xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", loaded.Proxmox.ApiTokenSecret);
    }
}
