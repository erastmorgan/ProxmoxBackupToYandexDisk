using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.FileOperations;
using ProxmoxBackupToYandexDisk.Core.Proxmox;
using ProxmoxBackupToYandexDisk.Core.Services;
using ProxmoxBackupToYandexDisk.Core.YandexDisk;

namespace ProxmoxBackupToYandexDisk.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all ProxmoxBackupToYandexDisk services to the service collection
    /// </summary>
    public static IServiceCollection AddProxmoxBackupServices(
        this IServiceCollection services,
        string proxmoxHost,
        int proxmoxPort = 8006)
    {
        // File operations
        services.AddSingleton<FileHasher>();
        services.AddSingleton<FileSplitter>();

        // Yandex Disk services
        services.AddHttpClient<YandexDiskOAuthService>();
        services.AddSingleton<YandexDiskTokenStorage>();
        services.AddSingleton<YandexDiskService>();

        // Proxmox service
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ProxmoxService>>();
            return new ProxmoxService(logger, proxmoxHost, proxmoxPort);
        });

        // SSH download service (for streaming uploads from remote Proxmox)
        services.AddSingleton<SshDownloadService>();

        // Main backup service
        services.AddSingleton<BackupService>();

        return services;
    }

    /// <summary>
    /// Adds Yandex Disk services only
    /// </summary>
    public static IServiceCollection AddYandexDiskServices(this IServiceCollection services)
    {
        services.AddHttpClient<YandexDiskOAuthService>();
        services.AddSingleton<YandexDiskTokenStorage>();
        services.AddSingleton<YandexDiskService>();

        return services;
    }

    /// <summary>
    /// Adds Proxmox services only
    /// </summary>
    public static IServiceCollection AddProxmoxServices(
        this IServiceCollection services,
        string host,
        int port = 8006)
    {
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ProxmoxService>>();
            return new ProxmoxService(logger, host, port);
        });

        return services;
    }

    /// <summary>
    /// Adds file operation services (splitting, hashing)
    /// </summary>
    public static IServiceCollection AddFileOperationServices(this IServiceCollection services)
    {
        services.AddSingleton<FileHasher>();
        services.AddSingleton<FileSplitter>();

        return services;
    }

    /// <summary>
    /// Adds SSH download service for remote Proxmox access and streaming uploads
    /// </summary>
    public static IServiceCollection AddSshServices(this IServiceCollection services)
    {
        services.AddSingleton<SshDownloadService>();

        return services;
    }
}
