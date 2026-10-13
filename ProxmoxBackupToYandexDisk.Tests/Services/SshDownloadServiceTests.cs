using Microsoft.Extensions.Logging;
using Moq;
using ProxmoxBackupToYandexDisk.Core.Services;

namespace ProxmoxBackupToYandexDisk.Tests.Services;

public class SshDownloadServiceTests : IDisposable
{
    private readonly Mock<ILogger<SshDownloadService>> _loggerMock;
    private readonly SshDownloadService _service;

    public SshDownloadServiceTests()
    {
        _loggerMock = new Mock<ILogger<SshDownloadService>>();
        _service = new SshDownloadService(_loggerMock.Object);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    [Fact]
    public void IsConfigured_WithoutConfigure_ReturnsFalse()
    {
        // Assert
        Assert.False(_service.IsConfigured);
    }

    [Fact]
    public void Configure_SetsIsConfiguredTrue()
    {
        // Act
        _service.Configure("192.168.1.100", "root", "password", 22);

        // Assert
        Assert.True(_service.IsConfigured);
    }

    [Fact]
    public void Configure_WithEmptyHost_StillSetsValues()
    {
        // Act
        _service.Configure("", "root", "password");

        // Assert
        Assert.False(_service.IsConfigured); // Empty host means not configured
    }

    [Fact]
    public void Configure_WithEmptyUsername_StillSetsValues()
    {
        // Act
        _service.Configure("host", "", "password");

        // Assert
        Assert.False(_service.IsConfigured); // Empty username means not configured
    }

    [Fact]
    public void GetFileInfo_WhenNotConfigured_ReturnsNull()
    {
        // Act
        var result = _service.GetFileInfo("/some/path");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetFileInfo_WhenNotConfigured_ReturnsError()
    {
        // Act
        var (success, error) = _service.TryGetFileInfo("/some/path", out var fileInfo);

        // Assert
        Assert.False(success);
        Assert.Equal("SSH not configured", error);
        Assert.Null(fileInfo);
    }

    [Fact]
    public void GetProxmoxVolumePath_WhenNotConfigured_ReturnsNull()
    {
        // Act
        var result = _service.GetProxmoxVolumePath("local:backup/test.vma.zst");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExecuteCommand_WhenNotConfigured_ReturnsNull()
    {
        // Act
        var result = _service.ExecuteCommand("ls -la");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenNotConfigured_ReturnsFalse()
    {
        // Act
        var result = await _service.TestConnectionAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DownloadFileAsync_WhenNotConfigured_ReturnsFalse()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            // Act
            var result = await _service.DownloadFileAsync("/remote/path", tempFile);

            // Assert
            Assert.False(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CreateStreamSource_WhenNotConfigured_ThrowsInvalidOperationException()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _service.CreateStreamSource("/some/path"));
    }

    [Theory]
    [InlineData(22)]
    [InlineData(2222)]
    [InlineData(22222)]
    public void Configure_WithDifferentPorts_AcceptsPorts(int port)
    {
        // Act
        _service.Configure("host", "user", "pass", port);

        // Assert
        Assert.True(_service.IsConfigured);
    }

    [Fact]
    public void Configure_CanBeCalledMultipleTimes()
    {
        // Act
        _service.Configure("host1", "user1", "pass1", 22);
        _service.Configure("host2", "user2", "pass2", 2222);

        // Assert - should not throw
        Assert.True(_service.IsConfigured);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Act & Assert - should not throw
        _service.Dispose();
        _service.Dispose();
    }
}
