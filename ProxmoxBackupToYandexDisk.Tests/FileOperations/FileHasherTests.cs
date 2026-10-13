using Microsoft.Extensions.Logging;
using Moq;
using ProxmoxBackupToYandexDisk.Core.FileOperations;

namespace ProxmoxBackupToYandexDisk.Tests.FileOperations;

public class FileHasherTests : IDisposable
{
    private readonly Mock<ILogger<FileHasher>> _loggerMock;
    private readonly FileHasher _hasher;
    private readonly string _testDir;

    public FileHasherTests()
    {
        _loggerMock = new Mock<ILogger<FileHasher>>();
        _hasher = new FileHasher(_loggerMock.Object);
        _testDir = Path.Combine(Path.GetTempPath(), $"hasher_test_{Guid.NewGuid():N}");
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
    public async Task ComputeHashAsync_EmptyFile_ReturnsCorrectHash()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "empty.txt");
        await File.WriteAllBytesAsync(filePath, []);

        // SHA-256 of empty file
        const string expectedHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        // Act
        var hash = await _hasher.ComputeHashAsync(filePath);

        // Assert
        Assert.Equal(expectedHash, hash);
    }

    [Fact]
    public async Task ComputeHashAsync_KnownContent_ReturnsCorrectHash()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "Hello, World!");

        // SHA-256 of "Hello, World!"
        const string expectedHash = "dffd6021bb2bd5b0af676290809ec3a53191dd81c7f70a4b28688a362182986f";

        // Act
        var hash = await _hasher.ComputeHashAsync(filePath);

        // Assert
        Assert.Equal(expectedHash, hash);
    }

    [Fact]
    public async Task ComputeHashAsync_WithProgress_ReportsProgress()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "large.bin");
        var data = new byte[1024 * 100]; // 100 KB
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(filePath, data);

        // Use synchronous callback collection to avoid timing issues
        var progressReports = new List<long>();
        var progress = new SynchronousProgress<long>(p => progressReports.Add(p));

        // Act
        await _hasher.ComputeHashAsync(filePath, progress);

        // Assert
        Assert.NotEmpty(progressReports);
        Assert.Equal(data.Length, progressReports.Last());
    }

    // Helper class for synchronous progress reporting in tests
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public async Task ComputeHashAsync_SameContent_SameHash()
    {
        // Arrange
        var content = "Test content for hashing"u8.ToArray();
        var filePath1 = Path.Combine(_testDir, "file1.bin");
        var filePath2 = Path.Combine(_testDir, "file2.bin");
        await File.WriteAllBytesAsync(filePath1, content);
        await File.WriteAllBytesAsync(filePath2, content);

        // Act
        var hash1 = await _hasher.ComputeHashAsync(filePath1);
        var hash2 = await _hasher.ComputeHashAsync(filePath2);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task ComputeHashAsync_DifferentContent_DifferentHash()
    {
        // Arrange
        var filePath1 = Path.Combine(_testDir, "file1.txt");
        var filePath2 = Path.Combine(_testDir, "file2.txt");
        await File.WriteAllTextAsync(filePath1, "Content A");
        await File.WriteAllTextAsync(filePath2, "Content B");

        // Act
        var hash1 = await _hasher.ComputeHashAsync(filePath1);
        var hash2 = await _hasher.ComputeHashAsync(filePath2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public async Task VerifyHashAsync_CorrectHash_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "verify.txt");
        await File.WriteAllTextAsync(filePath, "Verify me");
        var correctHash = await _hasher.ComputeHashAsync(filePath);

        // Act
        var isValid = await _hasher.VerifyHashAsync(filePath, correctHash);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public async Task VerifyHashAsync_IncorrectHash_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "verify.txt");
        await File.WriteAllTextAsync(filePath, "Verify me");
        const string incorrectHash = "0000000000000000000000000000000000000000000000000000000000000000";

        // Act
        var isValid = await _hasher.VerifyHashAsync(filePath, incorrectHash);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public async Task ComputeHashAsync_FromStream_MatchesFileHash()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "stream.bin");
        var data = new byte[1024];
        new Random(123).NextBytes(data);
        await File.WriteAllBytesAsync(filePath, data);

        // Act
        var fileHash = await _hasher.ComputeHashAsync(filePath);

        await using var stream = File.OpenRead(filePath);
        var streamHash = await _hasher.ComputeHashAsync(stream);

        // Assert
        Assert.Equal(fileHash, streamHash);
    }

    [Fact]
    public async Task ComputeHashAsync_WithOffsetAndLength_ComputesPartialHash()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "partial.bin");
        var data = new byte[1000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 256);
        await File.WriteAllBytesAsync(filePath, data);

        // Act
        var fullHash = await _hasher.ComputeHashAsync(filePath);
        var partialHash = await _hasher.ComputeHashAsync(filePath, 100, 500);

        // Assert
        Assert.NotEqual(fullHash, partialHash);
        Assert.Equal(64, partialHash.Length); // SHA-256 hex length
    }

    [Fact]
    public async Task ComputeHashAsync_Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "cancel.bin");
        var data = new byte[1024 * 1024]; // 1 MB
        await File.WriteAllBytesAsync(filePath, data);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        // Use ThrowsAnyAsync because TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _hasher.ComputeHashAsync(filePath, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ComputeHashAsync_NonExistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "nonexistent.file");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _hasher.ComputeHashAsync(nonExistentPath));
    }
}
