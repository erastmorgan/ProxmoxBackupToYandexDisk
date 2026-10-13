using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using ProxmoxBackupToYandexDisk.Core.Services;

namespace ProxmoxBackupToYandexDisk.Tests.Services;

public class TelegramNotificationServiceTests
{
    private readonly Mock<ILogger<TelegramNotificationService>> _loggerMock;

    public TelegramNotificationServiceTests()
    {
        _loggerMock = new Mock<ILogger<TelegramNotificationService>>();
    }

    private static Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, string content = "{\"ok\":true}")
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
        return handlerMock;
    }

    [Fact]
    public void Configure_SetsIsConfiguredTrue()
    {
        // Arrange
        var httpClient = new HttpClient(CreateMockHandler(System.Net.HttpStatusCode.OK).Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);

        // Act
        service.Configure("123456:ABC-DEF", "-1001234567890");

        // Assert
        Assert.True(service.IsConfigured);
    }

    [Fact]
    public void IsConfigured_WithoutConfigure_ReturnsFalse()
    {
        // Arrange
        var httpClient = new HttpClient(CreateMockHandler(System.Net.HttpStatusCode.OK).Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);

        // Assert
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public async Task SendNotificationAsync_WhenNotConfigured_DoesNotThrow()
    {
        // Arrange
        var httpClient = new HttpClient(CreateMockHandler(System.Net.HttpStatusCode.OK).Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);

        // Act - should not throw and should not make HTTP request
        await service.SendNotificationAsync("Test message");

        // Assert - just verify it completed without exception
    }

    [Fact]
    public async Task SendNotificationAsync_WithValidConfig_SendsRequest()
    {
        // Arrange
        var handlerMock = CreateMockHandler(HttpStatusCode.OK);
        var httpClient = new HttpClient(handlerMock.Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);
        service.Configure("123456:ABC-DEF", "-1001234567890");

        // Act - SendNotificationAsync returns Task (void), not Task<bool>
        await service.SendNotificationAsync("Test message");

        // Assert
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri!.ToString().Contains("123456:ABC-DEF") &&
                req.RequestUri.ToString().Contains("sendMessage")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendNotificationAsync_OnApiError_DoesNotThrow()
    {
        // Arrange
        var handlerMock = CreateMockHandler(HttpStatusCode.BadRequest, "{\"ok\":false}");
        var httpClient = new HttpClient(handlerMock.Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);
        service.Configure("123456:ABC-DEF", "-1001234567890");

        // Act - should not throw, service handles errors internally
        await service.SendNotificationAsync("Test message");

        // Assert - verify the request was made
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendNotificationAsync_OnNetworkError_DoesNotThrow()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var httpClient = new HttpClient(handlerMock.Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);
        service.Configure("123456:ABC-DEF", "-1001234567890");

        // Act - should not throw, service handles errors internally
        await service.SendNotificationAsync("Test message");

        // Assert - if we got here without exception, the test passed
    }

    [Fact]
    public async Task SendBackupSuccessAsync_FormatsMessageCorrectly()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"ok\":true}")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);
        service.Configure("123456:ABC-DEF", "-1001234567890");

        // Act
        await service.SendBackupSuccessAsync("TestVM", 100, "local", TimeSpan.FromMinutes(5));

        // Assert
        Assert.NotNull(capturedRequest);
        var content = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("TestVM", content);
        Assert.Contains("100", content);
    }

    [Fact]
    public async Task SendBackupErrorAsync_FormatsMessageCorrectly()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"ok\":true}")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);
        service.Configure("123456:ABC-DEF", "-1001234567890");

        // Act
        await service.SendBackupErrorAsync("FailedVM", 101, "pve", "Connection timeout");

        // Assert
        Assert.NotNull(capturedRequest);
        var content = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("FailedVM", content);
        Assert.Contains("Connection timeout", content);
    }

    [Fact]
    public async Task TestConnectionAsync_OnSuccess_ReturnsTrue()
    {
        // Arrange
        var handlerMock = CreateMockHandler(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"id\":123}}");
        var httpClient = new HttpClient(handlerMock.Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);
        service.Configure("123456:ABC-DEF", "-1001234567890");

        // Act
        var result = await service.TestConnectionAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TestConnectionAsync_OnFailure_ReturnsFalse()
    {
        // Arrange
        var handlerMock = CreateMockHandler(HttpStatusCode.Unauthorized, "{\"ok\":false}");
        var httpClient = new HttpClient(handlerMock.Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);
        service.Configure("invalid-token", "-1001234567890");

        // Act
        var result = await service.TestConnectionAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SendSchedulerErrorAsync_FormatsMessageCorrectly()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"ok\":true}")
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var service = new TelegramNotificationService(_loggerMock.Object, httpClient);
        service.Configure("123456:ABC-DEF", "-1001234567890");

        // Act
        await service.SendSchedulerErrorAsync("Daily Backup - API connection failed");

        // Assert
        Assert.NotNull(capturedRequest);
        var content = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("Daily Backup", content);
        Assert.Contains("API connection failed", content);
    }
}
