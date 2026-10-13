using Microsoft.Extensions.Logging;
using NCrontab;

namespace ProxmoxBackupToYandexDisk.Core.Services;

/// <summary>
/// Background service for scheduled backup jobs.
/// Designed to be fault-tolerant - continues running even if individual jobs fail.
/// </summary>
public sealed class SchedulerService : IDisposable
{
    private readonly ILogger<SchedulerService> _logger;
    private readonly TelegramNotificationService _telegramService;
    private readonly List<ScheduledJob> _jobs = [];
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private bool _isRunning;

    public event Func<ScheduledBackupJob, CancellationToken, Task<bool>>? OnJobExecute;

    public SchedulerService(
        ILogger<SchedulerService> logger,
        TelegramNotificationService telegramService)
    {
        _logger = logger;
        _telegramService = telegramService;
    }

    public bool IsRunning => _isRunning;

    /// <summary>
    /// Configures the scheduler with jobs from config
    /// </summary>
    public void Configure(SchedulerConfig config)
    {
        _jobs.Clear();

        if (!config.Enabled)
        {
            _logger.LogInformation("Scheduler is disabled");
            return;
        }

        foreach (var jobConfig in config.Jobs.Where(j => j.Enabled))
        {
            try
            {
                var schedule = CrontabSchedule.Parse(jobConfig.CronExpression,
                    new CrontabSchedule.ParseOptions { IncludingSeconds = false });

                _jobs.Add(new ScheduledJob
                {
                    Config = jobConfig,
                    Schedule = schedule,
                    NextRun = schedule.GetNextOccurrence(DateTime.Now)
                });

                _logger.LogInformation(
                    "Scheduled job '{Name}': {Cron} (next run: {NextRun})",
                    jobConfig.Name, jobConfig.CronExpression,
                    schedule.GetNextOccurrence(DateTime.Now));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invalid cron expression for job '{Name}': {Cron}",
                    jobConfig.Name, jobConfig.CronExpression);
            }
        }

        _logger.LogInformation("Scheduler configured with {Count} job(s)", _jobs.Count);
    }

    /// <summary>
    /// Starts the scheduler background loop
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            _logger.LogWarning("Scheduler is already running");
            return;
        }

        if (_jobs.Count == 0)
        {
            _logger.LogWarning("No jobs configured, scheduler not started");
            return;
        }

        _cts = new CancellationTokenSource();
        _isRunning = true;
        _runningTask = RunSchedulerLoopAsync(_cts.Token);

        _logger.LogInformation("Scheduler started");
    }

    /// <summary>
    /// Stops the scheduler
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _logger.LogInformation("Stopping scheduler...");
        _cts?.Cancel();

        if (_runningTask != null)
        {
            try
            {
                await _runningTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Scheduler stop timed out");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _isRunning = false;
        _logger.LogInformation("Scheduler stopped");
    }

    /// <summary>
    /// Gets the next scheduled run times
    /// </summary>
    public IEnumerable<(string JobName, DateTime NextRun)> GetNextRuns()
    {
        return _jobs
            .OrderBy(j => j.NextRun)
            .Select(j => (j.Config.Name, j.NextRun));
    }

    private async Task RunSchedulerLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scheduler loop started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;

                foreach (var job in _jobs.ToList())
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    if (now >= job.NextRun)
                    {
                        // Time to run this job
                        _logger.LogInformation("Executing scheduled job: {Name}", job.Config.Name);

                        try
                        {
                            await ExecuteJobAsync(job, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Job '{Name}' failed with exception", job.Config.Name);

                            // Send notification but don't let it break the scheduler
                            await SafeSendNotificationAsync(
                                $"Scheduled job '{job.Config.Name}' failed: {ex.Message}");
                        }

                        // Calculate next run time
                        job.NextRun = job.Schedule.GetNextOccurrence(DateTime.Now);
                        _logger.LogDebug("Job '{Name}' next run: {NextRun}", job.Config.Name, job.NextRun);
                    }
                }

                // Sleep for a short interval before checking again
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the scheduler loop crash
                _logger.LogError(ex, "Error in scheduler loop");
                await SafeSendNotificationAsync($"Scheduler error: {ex.Message}");

                // Wait before retrying to avoid tight loop on persistent errors
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Scheduler loop ended");
    }

    private async Task ExecuteJobAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;

        try
        {
            if (OnJobExecute == null)
            {
                _logger.LogWarning("No job executor configured");
                return;
            }

            var success = await OnJobExecute(job.Config, cancellationToken);
            var duration = DateTime.Now - startTime;

            if (success)
            {
                _logger.LogInformation(
                    "Job '{Name}' completed successfully in {Duration}",
                    job.Config.Name, duration);
            }
            else
            {
                _logger.LogWarning("Job '{Name}' completed with errors", job.Config.Name);
                await SafeSendNotificationAsync(
                    $"Scheduled job '{job.Config.Name}' completed with errors");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job '{Name}' was cancelled", job.Config.Name);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job '{Name}' failed", job.Config.Name);
            await SafeSendNotificationAsync(
                $"Scheduled job '{job.Config.Name}' failed: {ex.Message}");
            throw;
        }
    }

    private async Task SafeSendNotificationAsync(string message)
    {
        try
        {
            await _telegramService.SendSchedulerErrorAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private sealed class ScheduledJob
    {
        public required ScheduledBackupJob Config { get; init; }
        public required CrontabSchedule Schedule { get; init; }
        public DateTime NextRun { get; set; }
    }
}
