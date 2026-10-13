using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.Models;

namespace ProxmoxBackupToYandexDisk.Core.YandexDisk;

/// <summary>
/// OAuth 2.0 authentication service for Yandex Disk
/// Uses browser-based authorization with local callback server
/// </summary>
public sealed class YandexDiskOAuthService(
    ILogger<YandexDiskOAuthService> logger,
    HttpClient httpClient)
{
    private const string AuthorizeUrl = "https://oauth.yandex.ru/authorize";
    private const string TokenUrl = "https://oauth.yandex.ru/token";
    private const string DefaultCallbackPort = "8765";

    /// <summary>
    /// Starts the OAuth flow by opening browser and waiting for callback
    /// </summary>
    public async Task<YandexDiskTokenResponse> AuthorizeAsync(
        string clientId,
        string clientSecret,
        int callbackPort = 8765,
        CancellationToken cancellationToken = default)
    {
        var redirectUri = $"http://localhost:{callbackPort}/callback";
        var state = Guid.NewGuid().ToString("N");

        // Build authorization URL
        var authUrl = $"{AuthorizeUrl}?response_type=code" +
                      $"&client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&state={state}" +
                      "&force_confirm=yes";

        logger.LogInformation("Starting OAuth authorization flow");
        logger.LogInformation("Opening browser for authorization...");

        // Start local HTTP listener for callback
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{callbackPort}/");
        listener.Start();

        // Open browser
        OpenBrowser(authUrl);

        Console.WriteLine();
        Console.WriteLine("Opening browser for Yandex authorization...");
        Console.WriteLine("If browser does not open automatically, please visit:");
        Console.WriteLine(authUrl);
        Console.WriteLine();
        Console.WriteLine("Waiting for authorization...");

        // Wait for callback
        string? authorizationCode = null;
        string? returnedState = null;

        try
        {
            var contextTask = listener.GetContextAsync();
            var completedTask = await Task.WhenAny(
                contextTask,
                Task.Delay(TimeSpan.FromMinutes(5), cancellationToken));

            if (completedTask != contextTask)
            {
                throw new TimeoutException("Authorization timed out. Please try again.");
            }

            var context = await contextTask;
            var request = context.Request;
            var response = context.Response;

            // Parse query parameters
            var query = request.Url?.Query ?? string.Empty;
            var queryParams = System.Web.HttpUtility.ParseQueryString(query);

            authorizationCode = queryParams["code"];
            returnedState = queryParams["state"];
            var error = queryParams["error"];

            // Send response to browser
            string responseHtml;
            if (!string.IsNullOrEmpty(error))
            {
                responseHtml = GetErrorHtml(error, queryParams["error_description"]);
                response.StatusCode = 400;
            }
            else if (string.IsNullOrEmpty(authorizationCode))
            {
                responseHtml = GetErrorHtml("missing_code", "Authorization code not received");
                response.StatusCode = 400;
            }
            else
            {
                responseHtml = GetSuccessHtml();
                response.StatusCode = 200;
            }

            var buffer = System.Text.Encoding.UTF8.GetBytes(responseHtml);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, cancellationToken);
            response.Close();
        }
        finally
        {
            listener.Stop();
        }

        // Validate state
        if (returnedState != state)
        {
            throw new InvalidOperationException("OAuth state mismatch. Possible CSRF attack.");
        }

        if (string.IsNullOrEmpty(authorizationCode))
        {
            throw new InvalidOperationException("Failed to receive authorization code.");
        }

        logger.LogInformation("Authorization code received, exchanging for tokens...");

        // Exchange code for tokens
        return await ExchangeCodeForTokensAsync(
            clientId,
            clientSecret,
            authorizationCode,
            redirectUri,
            cancellationToken);
    }

    /// <summary>
    /// Exchanges authorization code for access and refresh tokens
    /// </summary>
    private async Task<YandexDiskTokenResponse> ExchangeCodeForTokensAsync(
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri
        });

        var response = await httpClient.PostAsync(TokenUrl, content, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Token exchange failed: {StatusCode} - {Content}",
                response.StatusCode, responseContent);
            throw new HttpRequestException($"Token exchange failed: {responseContent}");
        }

        var tokenResponse = JsonSerializer.Deserialize<YandexDiskTokenResponse>(responseContent)
            ?? throw new InvalidOperationException("Failed to parse token response");

        logger.LogInformation("Successfully obtained access token");
        return tokenResponse;
    }

    /// <summary>
    /// Refreshes an expired access token
    /// </summary>
    public async Task<YandexDiskTokenResponse> RefreshTokenAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Refreshing access token...");

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        });

        var response = await httpClient.PostAsync(TokenUrl, content, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Token refresh failed: {StatusCode} - {Content}",
                response.StatusCode, responseContent);
            throw new HttpRequestException($"Token refresh failed: {responseContent}");
        }

        var tokenResponse = JsonSerializer.Deserialize<YandexDiskTokenResponse>(responseContent)
            ?? throw new InvalidOperationException("Failed to parse token response");

        logger.LogInformation("Successfully refreshed access token");
        return tokenResponse;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            // Try to open browser on different platforms
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception)
        {
            // Ignore errors - user can manually open the URL
        }
    }

    private static string GetSuccessHtml() => """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <title>Authorization Successful</title>
            <style>
                body {
                    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                    display: flex;
                    justify-content: center;
                    align-items: center;
                    height: 100vh;
                    margin: 0;
                    background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
                }
                .container {
                    text-align: center;
                    background: white;
                    padding: 3rem;
                    border-radius: 1rem;
                    box-shadow: 0 10px 40px rgba(0,0,0,0.2);
                }
                .success {
                    color: #10b981;
                    font-size: 4rem;
                    margin-bottom: 1rem;
                }
                h1 { color: #1f2937; margin-bottom: 0.5rem; }
                p { color: #6b7280; }
            </style>
        </head>
        <body>
            <div class="container">
                <div class="success">✓</div>
                <h1>Authorization Successful!</h1>
                <p>You can close this window and return to the application.</p>
            </div>
        </body>
        </html>
        """;

    private static string GetErrorHtml(string error, string? description)
    {
        var desc = description ?? "An error occurred during authorization.";
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>Authorization Failed</title>
                <style>
                    body {
                        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                        display: flex;
                        justify-content: center;
                        align-items: center;
                        height: 100vh;
                        margin: 0;
                        background: linear-gradient(135deg, #ef4444 0%, #dc2626 100%);
                    }
                    .container {
                        text-align: center;
                        background: white;
                        padding: 3rem;
                        border-radius: 1rem;
                        box-shadow: 0 10px 40px rgba(0,0,0,0.2);
                    }
                    .error {
                        color: #ef4444;
                        font-size: 4rem;
                        margin-bottom: 1rem;
                    }
                    h1 { color: #1f2937; margin-bottom: 0.5rem; }
                    p { color: #6b7280; }
                    .details { font-family: monospace; background: #f3f4f6; padding: 0.5rem 1rem; border-radius: 0.25rem; margin-top: 1rem; }
                </style>
            </head>
            <body>
                <div class="container">
                    <div class="error">✗</div>
                    <h1>Authorization Failed</h1>
                    <p>{{desc}}</p>
                    <div class="details">{{error}}</div>
                </div>
            </body>
            </html>
            """;
    }
}
