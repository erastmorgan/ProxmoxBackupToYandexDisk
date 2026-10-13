using System.Text.Json;
using ProxmoxBackupToYandexDisk.Core.Models;

namespace ProxmoxBackupToYandexDisk.Tests.Models;

public class ProxmoxModelsTests
{
    [Fact]
    public void ProxmoxVm_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var vm = new ProxmoxVm();

        // Assert
        Assert.Equal(0, vm.VmId);
        Assert.Equal("", vm.Name);
        Assert.Equal("", vm.Status);
        Assert.Equal("", vm.Type); // Type is a string, not enum
    }

    [Fact]
    public void ProxmoxStorage_Enabled_DefaultsToOne()
    {
        // Arrange & Act
        var storage = new ProxmoxStorage();

        // Assert
        Assert.Equal(1, storage.Enabled);
        Assert.Equal(1, storage.Active);
    }

    [Fact]
    public void ProxmoxBackupInfo_Serialization_RoundTrip()
    {
        // Arrange
        var backup = new ProxmoxBackupInfo
        {
            VolId = "local:backup/vzdump-qemu-100-2024_01_15.vma.zst",
            Format = "vma",
            Size = 1024 * 1024 * 1024L,
            CTime = 1705305600, // Unix timestamp
            VmId = 100
        };

        // Act
        var json = JsonSerializer.Serialize(backup);
        var deserialized = JsonSerializer.Deserialize<ProxmoxBackupInfo>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(backup.VolId, deserialized.VolId);
        Assert.Equal(backup.Format, deserialized.Format);
        Assert.Equal(backup.Size, deserialized.Size);
        Assert.Equal(backup.CTime, deserialized.CTime);
        Assert.Equal(backup.VmId, deserialized.VmId);
    }

    [Theory]
    [InlineData(ProxmoxVmType.Qemu, "qemu")]
    [InlineData(ProxmoxVmType.Lxc, "lxc")]
    public void ProxmoxVmType_Values_AreCorrect(ProxmoxVmType type, string expectedName)
    {
        // Act
        var name = type.ToString().ToLowerInvariant();

        // Assert
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void ProxmoxStorage_WithContent_ParsesCorrectly()
    {
        // Arrange
        var json = """
            {
                "storage": "local2",
                "type": "dir",
                "content": "backup,images",
                "enabled": 1,
                "active": 1
            }
            """;

        // Act
        var storage = JsonSerializer.Deserialize<ProxmoxStorage>(json);

        // Assert
        Assert.NotNull(storage);
        Assert.Equal("local2", storage.Storage);
        Assert.Equal("dir", storage.Type);
        Assert.Equal("backup,images", storage.Content);
    }
}
