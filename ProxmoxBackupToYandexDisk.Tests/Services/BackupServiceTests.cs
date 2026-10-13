using Microsoft.Extensions.Logging;
using Moq;
using ProxmoxBackupToYandexDisk.Core.FileOperations;
using ProxmoxBackupToYandexDisk.Core.Services;

namespace ProxmoxBackupToYandexDisk.Tests.Services;

/// <summary>
/// Tests for BackupService focusing on scenarios that don't require
/// complex external service mocking (Yandex, Proxmox)
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly Mock<ILogger<BackupService>> _loggerMock;
    private readonly Mock<ILogger<FileSplitter>> _splitterLoggerMock;
    private readonly Mock<ILogger<FileHasher>> _hasherLoggerMock;
    private readonly Mock<ILogger<SshDownloadService>> _sshLoggerMock;

    private readonly string _testDir;

    public BackupServiceTests()
    {
        _loggerMock = new Mock<ILogger<BackupService>>();
        _splitterLoggerMock = new Mock<ILogger<FileSplitter>>();
        _hasherLoggerMock = new Mock<ILogger<FileHasher>>();
        _sshLoggerMock = new Mock<ILogger<SshDownloadService>>();

        _testDir = Path.Combine(Path.GetTempPath(), $"backup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); }
            catch { /* ignore */ }
        }
    }

    [Fact]
    public void FileHasher_CanBeCreated()
    {
        // Arrange & Act
        var fileHasher = new FileHasher(_hasherLoggerMock.Object);

        // Assert
        Assert.NotNull(fileHasher);
    }

    [Fact]
    public void FileSplitter_CanBeCreated()
    {
        // Arrange
        var fileHasher = new FileHasher(_hasherLoggerMock.Object);

        // Act
        var fileSplitter = new FileSplitter(_splitterLoggerMock.Object, fileHasher);

        // Assert
        Assert.NotNull(fileSplitter);
    }

    [Fact]
    public void SshDownloadService_WhenNotConfigured_IsConfiguredReturnsFalse()
    {
        // Arrange
        var sshService = new SshDownloadService(_sshLoggerMock.Object);

        // Assert
        Assert.False(sshService.IsConfigured);
    }

    [Fact]
    public void SshDownloadService_WhenConfigured_IsConfiguredReturnsTrue()
    {
        // Arrange
        var sshService = new SshDownloadService(_sshLoggerMock.Object);

        // Act
        sshService.Configure("192.168.1.100", "root", "password", 22);

        // Assert
        Assert.True(sshService.IsConfigured);
    }

    [Fact]
    public void SshDownloadService_GetFileInfo_WhenNotConfigured_ReturnsNull()
    {
        // Arrange
        var sshService = new SshDownloadService(_sshLoggerMock.Object);

        // Act
        var result = sshService.GetFileInfo("/some/path");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void SshDownloadService_TryGetFileInfo_WhenNotConfigured_ReturnsError()
    {
        // Arrange
        var sshService = new SshDownloadService(_sshLoggerMock.Object);

        // Act
        var (success, error) = sshService.TryGetFileInfo("/some/path", out var fileInfo);

        // Assert
        Assert.False(success);
        Assert.Equal("SSH not configured", error);
        Assert.Null(fileInfo);
    }

    [Fact]
    public async Task SshDownloadService_TestConnectionAsync_WhenNotConfigured_ReturnsFalse()
    {
        // Arrange
        var sshService = new SshDownloadService(_sshLoggerMock.Object);

        // Act
        var result = await sshService.TestConnectionAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void BackupProgress_Record_WorksCorrectly()
    {
        // Arrange & Act
        var progress1 = new BackupProgress("Step 1", 50);
        var progress2 = new BackupProgress("Step 1", 50);
        var progress3 = new BackupProgress("Step 2", 75);

        // Assert
        Assert.Equal("Step 1", progress1.Message);
        Assert.Equal(50, progress1.PercentComplete);
        Assert.Equal(progress1, progress2); // Records with same values are equal
        Assert.NotEqual(progress1, progress3);
    }

    [Fact]
    public async Task FileHasher_ComputeHashAsync_ReturnsValidHash()
    {
        // Arrange
        var fileHasher = new FileHasher(_hasherLoggerMock.Object);
        var testFile = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "Hello, World!");

        // Act
        var hash = await fileHasher.ComputeHashAsync(testFile);

        // Assert
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length); // SHA256 produces 64 hex characters
        // SHA256 of "Hello, World!"
        Assert.Equal("dffd6021bb2bd5b0af676290809ec3a53191dd81c7f70a4b28688a362182986f", hash);
    }

    [Fact]
    public async Task FileHasher_VerifyHashAsync_WithCorrectHash_ReturnsTrue()
    {
        // Arrange
        var fileHasher = new FileHasher(_hasherLoggerMock.Object);
        var testFile = Path.Combine(_testDir, "verify.txt");
        await File.WriteAllTextAsync(testFile, "Test content");
        var hash = await fileHasher.ComputeHashAsync(testFile);

        // Act
        var result = await fileHasher.VerifyHashAsync(testFile, hash);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task FileHasher_VerifyHashAsync_WithIncorrectHash_ReturnsFalse()
    {
        // Arrange
        var fileHasher = new FileHasher(_hasherLoggerMock.Object);
        var testFile = Path.Combine(_testDir, "verify.txt");
        await File.WriteAllTextAsync(testFile, "Test content");

        // Act
        var result = await fileHasher.VerifyHashAsync(testFile, "0000000000000000000000000000000000000000000000000000000000000000");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task FileHasher_ComputeHashAsync_SameContent_SameHash()
    {
        // Arrange
        var fileHasher = new FileHasher(_hasherLoggerMock.Object);
        var content = "Test content for hashing"u8.ToArray();
        var file1 = Path.Combine(_testDir, "file1.bin");
        var file2 = Path.Combine(_testDir, "file2.bin");
        await File.WriteAllBytesAsync(file1, content);
        await File.WriteAllBytesAsync(file2, content);

        // Act
        var hash1 = await fileHasher.ComputeHashAsync(file1);
        var hash2 = await fileHasher.ComputeHashAsync(file2);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task FileHasher_ComputeHashAsync_DifferentContent_DifferentHash()
    {
        // Arrange
        var fileHasher = new FileHasher(_hasherLoggerMock.Object);
        var file1 = Path.Combine(_testDir, "diff1.txt");
        var file2 = Path.Combine(_testDir, "diff2.txt");
        await File.WriteAllTextAsync(file1, "Content A");
        await File.WriteAllTextAsync(file2, "Content B");

        // Act
        var hash1 = await fileHasher.ComputeHashAsync(file1);
        var hash2 = await fileHasher.ComputeHashAsync(file2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }
}
