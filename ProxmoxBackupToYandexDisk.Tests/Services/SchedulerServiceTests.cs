using Microsoft.Extensions.Logging;
using Moq;
using ProxmoxBackupToYandexDisk.Core.Services;

namespace ProxmoxBackupToYandexDisk.Tests.Services;

public class SchedulerServiceTests : IAsyncDisposable
{
    private readonly Mock<ILogger<SchedulerService>> _loggerMock;
    private readonly Mock<ILogger<TelegramNotificationService>> _telegramLoggerMock;
    private readonly TelegramNotificationService _telegramService;
    private readonly SchedulerService _scheduler;

    public SchedulerServiceTests()
    {
        _loggerMock = new Mock<ILogger<SchedulerService>>();
        _telegramLoggerMock = new Mock<ILogger<TelegramNotificationService>>();
        var httpClient = new HttpClient();
        _telegramService = new TelegramNotificationService(_telegramLoggerMock.Object, httpClient);
        _scheduler = new SchedulerService(_loggerMock.Object, _telegramService);
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.StopAsync();
        _scheduler.Dispose();
    }

    [Fact]
    public void Configure_WithValidConfig_ConfiguresJobs()
    {
        // Arrange
        var config = new SchedulerConfig
        {
            Enabled = true,
            Jobs =
            [
                new ScheduledBackupJob
                {
                    Name = "Test Job",
                    CronExpression = "0 * * * *", // Every hour
                    NodeName = "pve",
                    VmIds = [100],
                    Storage = "local",
                    Enabled = true
                }
            ]
        };

        // Act
        _scheduler.Configure(config);
        var nextRuns = _scheduler.GetNextRuns().ToList();

        // Assert
        Assert.Single(nextRuns);
        Assert.Equal("Test Job", nextRuns[0].JobName);
    }

    [Fact]
    public void Configure_WithDisabledConfig_HasNoJobs()
    {
        // Arrange
        var config = new SchedulerConfig { Enabled = false };

        // Act
        _scheduler.Configure(config);
        var nextRuns = _scheduler.GetNextRuns().ToList();

        // Assert
        Assert.Empty(nextRuns);
    }

    [Fact]
    public void Configure_WithEmptyJobs_HasNoJobs()
    {
        // Arrange
        var config = new SchedulerConfig
        {
            Enabled = true,
            Jobs = []
        };

        // Act
        _scheduler.Configure(config);
        var nextRuns = _scheduler.GetNextRuns().ToList();

        // Assert
        Assert.Empty(nextRuns);
    }

    [Fact]
    public void GetNextRuns_ReturnsCorrectNextExecutionTimes()
    {
        // Arrange
        var config = new SchedulerConfig
        {
            Enabled = true,
            Jobs =
            [
                new ScheduledBackupJob
                {
                    Name = "Hourly Job",
                    CronExpression = "0 * * * *", // Every hour at :00
                    NodeName = "pve",
                    VmIds = [100],
                    Storage = "local",
                    Enabled = true
                }
            ]
        };
        _scheduler.Configure(config);

        // Act
        var nextRuns = _scheduler.GetNextRuns().ToList();

        // Assert
        Assert.Single(nextRuns);
        Assert.Equal("Hourly Job", nextRuns[0].JobName);
        Assert.Equal(0, nextRuns[0].NextRun.Minute); // Should be at :00
    }

    [Fact]
    public void GetNextRuns_WithDisabledJob_ExcludesDisabledJob()
    {
        // Arrange
        var config = new SchedulerConfig
        {
            Enabled = true,
            Jobs =
            [
                new ScheduledBackupJob
                {
                    Name = "Enabled Job",
                    CronExpression = "0 * * * *",
                    NodeName = "pve",
                    VmIds = [100],
                    Storage = "local",
                    Enabled = true
                },
                new ScheduledBackupJob
                {
                    Name = "Disabled Job",
                    CronExpression = "30 * * * *",
                    NodeName = "pve",
                    VmIds = [101],
                    Storage = "local",
                    Enabled = false
                }
            ]
        };
        _scheduler.Configure(config);

        // Act
        var nextRuns = _scheduler.GetNextRuns().ToList();

        // Assert
        Assert.Single(nextRuns);
        Assert.Equal("Enabled Job", nextRuns[0].JobName);
    }

    [Fact]
    public void Configure_WithInvalidCronExpression_SkipsInvalidJob()
    {
        // Arrange
        var config = new SchedulerConfig
        {
            Enabled = true,
            Jobs =
            [
                new ScheduledBackupJob
                {
                    Name = "Invalid Job",
                    CronExpression = "invalid cron",
                    NodeName = "pve",
                    VmIds = [100],
                    Storage = "local",
                    Enabled = true
                }
            ]
        };

        // Act
        _scheduler.Configure(config);
        var nextRuns = _scheduler.GetNextRuns().ToList();

        // Assert - invalid job should be skipped
        Assert.Empty(nextRuns);
    }

    [Fact]
    public void Start_WhenNoJobsConfigured_DoesNotStart()
    {
        // Arrange - no configuration

        // Act
        _scheduler.Start();

        // Assert - should not throw, IsRunning should be false since no jobs
        Assert.False(_scheduler.IsRunning);
    }

    [Fact]
    public async Task StopAsync_WhenNotStarted_CompletesSuccessfully()
    {
        // Act & Assert - should not throw
        await _scheduler.StopAsync();
    }

    [Theory]
    [InlineData("0 0 * * *", 0, 0)] // Daily at midnight
    [InlineData("0 12 * * *", 12, 0)] // Daily at noon
    [InlineData("30 6 * * *", 6, 30)] // Daily at 6:30
    public void GetNextRuns_ValidCronExpressions_ParsesCorrectly(string cron, int expectedHour, int expectedMinute)
    {
        // Arrange
        var config = new SchedulerConfig
        {
            Enabled = true,
            Jobs =
            [
                new ScheduledBackupJob
                {
                    Name = "Test",
                    CronExpression = cron,
                    NodeName = "pve",
                    VmIds = [100],
                    Storage = "local",
                    Enabled = true
                }
            ]
        };
        _scheduler.Configure(config);

        // Act
        var nextRuns = _scheduler.GetNextRuns().ToList();

        // Assert
        Assert.Single(nextRuns);
        Assert.Equal(expectedHour, nextRuns[0].NextRun.Hour);
        Assert.Equal(expectedMinute, nextRuns[0].NextRun.Minute);
    }

    [Fact]
    public void Configure_MultipleJobs_AllJobsScheduled()
    {
        // Arrange
        var config = new SchedulerConfig
        {
            Enabled = true,
            Jobs =
            [
                new ScheduledBackupJob
                {
                    Name = "Job 1",
                    CronExpression = "0 1 * * *",
                    NodeName = "pve",
                    VmIds = [100],
                    Storage = "local",
                    Enabled = true
                },
                new ScheduledBackupJob
                {
                    Name = "Job 2",
                    CronExpression = "0 2 * * *",
                    NodeName = "pve",
                    VmIds = [101],
                    Storage = "local",
                    Enabled = true
                },
                new ScheduledBackupJob
                {
                    Name = "Job 3",
                    CronExpression = "0 3 * * *",
                    NodeName = "pve",
                    VmIds = [102],
                    Storage = "local",
                    Enabled = true
                }
            ]
        };
        _scheduler.Configure(config);

        // Act
        var nextRuns = _scheduler.GetNextRuns().ToList();

        // Assert
        Assert.Equal(3, nextRuns.Count);
        Assert.Contains(nextRuns, r => r.JobName == "Job 1");
        Assert.Contains(nextRuns, r => r.JobName == "Job 2");
        Assert.Contains(nextRuns, r => r.JobName == "Job 3");
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Act & Assert - should not throw
        _scheduler.Dispose();
        _scheduler.Dispose();
    }
}
