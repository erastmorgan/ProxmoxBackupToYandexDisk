using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProxmoxBackupToYandexDisk.Core.Services;

/// <summary>
/// Service for sending notifications to Telegram.
/// Designed to be fault-tolerant - never throws exceptions to the caller.
/// </summary>
public sealed class TelegramNotificationService
{
    private readonly ILogger<TelegramNotificationService> _logger;
    private readonly HttpClient _httpClient;
    private string? _botToken;
    private string? _chatId;

    public TelegramNotificationService(
        ILogger<TelegramNotificationService> logger,
        HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Configures the Telegram notification settings
    /// </summary>
    public void Configure(string? botToken, string? chatId)
    {
        _botToken = botToken;
        _chatId = chatId;

        if (IsConfigured)
        {
            _logger.LogInformation("Telegram notifications configured for chat {ChatId}", chatId);
        }
        else
        {
            _logger.LogDebug("Telegram notifications not configured");
        }
    }

    /// <summary>
    /// Checks if Telegram is configured
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_botToken) && !string.IsNullOrEmpty(_chatId);

    /// <summary>
    /// Sends a notification message. Never throws exceptions.
    /// </summary>
    public async Task SendNotificationAsync(string message, bool isError = false)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Telegram not configured, skipping notification");
            return;
        }

        try
        {
            var emoji = isError ? "❌" : "✅";
            var fullMessage = $"{emoji} *Proxmox Backup*\n\n{EscapeMarkdown(message)}";

            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new
            {
                chat_id = _chatId,
                text = fullMessage,
                parse_mode = "Markdown"
            };

            var response = await _httpClient.PostAsJsonAsync(url, payload);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to send Telegram notification: {StatusCode} - {Error}",
                    response.StatusCode, errorContent);
            }
            else
            {
                _logger.LogDebug("Telegram notification sent successfully");
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Telegram notification timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Telegram notification failed: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error sending Telegram notification");
        }
    }

    /// <summary>
    /// Sends a backup success notification
    /// </summary>
    public Task SendBackupSuccessAsync(string vmName, int vmId, string nodeName, TimeSpan duration)
    {
        var message = $"Backup completed successfully\n\n" +
                      $"• VM: {vmName} ({vmId})\n" +
                      $"• Node: {nodeName}\n" +
                      $"• Duration: {duration:hh\\:mm\\:ss}";

        return SendNotificationAsync(message, isError: false);
    }

    /// <summary>
    /// Sends a backup error notification
    /// </summary>
    public Task SendBackupErrorAsync(string vmName, int vmId, string nodeName, string errorMessage)
    {
        var message = $"Backup failed\n\n" +
                      $"• VM: {vmName} ({vmId})\n" +
                      $"• Node: {nodeName}\n" +
                      $"• Error: {errorMessage}";

        return SendNotificationAsync(message, isError: true);
    }

    /// <summary>
    /// Sends an upload error notification
    /// </summary>
    public Task SendUploadErrorAsync(string vmName, int vmId, string errorMessage)
    {
        var message = $"Upload to Yandex Disk failed\n\n" +
                      $"• VM: {vmName} ({vmId})\n" +
                      $"• Error: {errorMessage}";

        return SendNotificationAsync(message, isError: true);
    }

    /// <summary>
    /// Sends a scheduler error notification
    /// </summary>
    public Task SendSchedulerErrorAsync(string errorMessage)
    {
        var message = $"Scheduler error\n\n{errorMessage}";
        return SendNotificationAsync(message, isError: true);
    }

    /// <summary>
    /// Tests the Telegram connection
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        if (!IsConfigured)
            return false;

        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/getMe";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Telegram connection test successful");
                return true;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Telegram connection test failed: {Error}", errorContent);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram connection test failed");
            return false;
        }
    }

    private static string EscapeMarkdown(string text)
    {
        // Escape special characters for Telegram Markdown
        return text
            .Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("[", "\\[")
            .Replace("`", "\\`");
    }
}
