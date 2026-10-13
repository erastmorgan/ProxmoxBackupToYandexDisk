using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProxmoxBackupToYandexDisk.Core.FileOperations;
using ProxmoxBackupToYandexDisk.Core.Models;
using ProxmoxBackupToYandexDisk.Core.Proxmox;
using ProxmoxBackupToYandexDisk.Core.Services;
using ProxmoxBackupToYandexDisk.Core.YandexDisk;
using Spectre.Console;

// Check for daemon mode first
var isDaemonMode = args.Contains("--daemon") || args.Contains("-d");

// Show help if requested
if (args.Contains("--help") || args.Contains("-h"))
{
    ShowHelp();
    return;
}

// Run tests if requested
if (args.Contains("--test"))
{
    var vmNameIndex = Array.IndexOf(args, "--vm");
    var vmName = vmNameIndex >= 0 && vmNameIndex + 1 < args.Length
        ? args[vmNameIndex + 1]
        : "PersonalDocsVM";

    var result = await ProxmoxBackupToYandexDisk.Console.TestRunner.RunTestsAsync(vmName);
    return;
}

// Display banner (only in interactive mode)
if (!isDaemonMode)
{
    AnsiConsole.Write(
        new FigletText("PVE Backup")
            .LeftJustified()
            .Color(Color.Blue));

    AnsiConsole.MarkupLine("[grey]Proxmox VE Backup to Yandex Disk[/]");
    AnsiConsole.MarkupLine("[grey]Supports Proxmox VE 6.0+ (QEMU VMs and LXC containers)[/]");
    AnsiConsole.WriteLine();
}

// Build host for config service
var configBuilder = Host.CreateApplicationBuilder();
configBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
configBuilder.Services.AddSingleton<AppConfigService>();
var configHost = configBuilder.Build();
var configService = configHost.Services.GetRequiredService<AppConfigService>();

// Load or create configuration
var config = await configService.LoadConfigAsync() ?? new AppConfig();
var configChanged = false;

// Check if we need to configure Proxmox
if (string.IsNullOrEmpty(config.Proxmox.Host))
{
    AnsiConsole.MarkupLine("[yellow]No saved configuration found. Let's set it up.[/]");
    AnsiConsole.WriteLine();
    await ConfigureProxmoxAsync();
    configChanged = true;
}

// Check if we need to configure Yandex
if (string.IsNullOrEmpty(config.Yandex.ClientId))
{
    await ConfigureYandexAsync();
    configChanged = true;
}

// Save config if changed
if (configChanged)
{
    await configService.SaveConfigAsync(config);
    AnsiConsole.MarkupLine("[green]Configuration saved![/]");
    AnsiConsole.WriteLine();
}

// Build main host
var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddConsole();

// Register services
builder.Services.AddHttpClient<YandexDiskOAuthService>();
builder.Services.AddHttpClient<TelegramNotificationService>();
builder.Services.AddSingleton<FileHasher>();
builder.Services.AddSingleton<FileSplitter>();
builder.Services.AddSingleton<YandexDiskTokenStorage>();
builder.Services.AddSingleton<YandexDiskOAuthService>();
builder.Services.AddSingleton<YandexDiskService>();
builder.Services.AddSingleton<AppConfigService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<SshDownloadService>();
builder.Services.AddSingleton<TelegramNotificationService>();
builder.Services.AddSingleton<SchedulerService>();

var host = builder.Build();

// Initialize services
ProxmoxService? proxmoxService = null;
YandexDiskService? yandexDiskService = null;
BackupService? backupService = null;
SshDownloadService? sshService = null;
TelegramNotificationService? telegramService = null;
SchedulerService? schedulerService = null;

// Try to connect
var connected = await TryConnectAsync();

if (!connected)
{
    if (isDaemonMode)
    {
        Console.WriteLine("ERROR: Could not establish connections. Exiting.");
        Environment.Exit(1);
    }
    AnsiConsole.MarkupLine("[red]Could not establish connections. Please check your settings.[/]");
    return;
}

// Daemon mode - run scheduler and wait for termination
if (isDaemonMode)
{
    await RunDaemonModeAsync();
    return;
}

// Main menu loop (interactive mode)
while (true)
{
    var choices = new List<string>
    {
        "Create and upload backup",
        "Upload existing backup file",
        "List backups on Yandex Disk",
        "Download and restore backup",
        "---",
        "Settings",
        "Exit"
    };

    var action = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("What would you like to do?")
            .AddChoices(choices));

    switch (action)
    {
        case "Create and upload backup":
            await CreateAndUploadBackupAsync();
            break;
        case "Upload existing backup file":
            await UploadExistingBackupAsync();
            break;
        case "List backups on Yandex Disk":
            await ListBackupsAsync();
            break;
        case "Download and restore backup":
            await DownloadAndRestoreAsync();
            break;
        case "Settings":
            await ShowSettingsMenuAsync();
            break;
        case "Exit":
            AnsiConsole.MarkupLine("[grey]Goodbye![/]");
            return;
    }

    AnsiConsole.WriteLine();
}

// === Connection Methods ===

async Task<bool> TryConnectAsync()
{
    // Create Proxmox service
    var proxmoxLogger = host.Services.GetRequiredService<ILogger<ProxmoxService>>();
    proxmoxService = new ProxmoxService(proxmoxLogger, config.Proxmox.Host, config.Proxmox.Port);

    // Authenticate with Proxmox
    var proxmoxConnected = false;
    await AnsiConsole.Status()
        .StartAsync("Connecting to Proxmox...", async _ =>
        {
            try
            {
                if (config.Proxmox.UseApiToken && !string.IsNullOrEmpty(config.Proxmox.ApiTokenId))
                {
                    proxmoxService.LoginWithApiToken(config.Proxmox.ApiTokenId, config.Proxmox.ApiTokenSecret!);
                    proxmoxConnected = true;
                }
                else
                {
                    proxmoxConnected = await proxmoxService.LoginAsync(
                        config.Proxmox.Username,
                        config.Proxmox.Password,
                        config.Proxmox.Realm);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Proxmox connection error: {ex.Message}[/]");
            }
        });

    if (!proxmoxConnected)
    {
        AnsiConsole.MarkupLine("[red]Failed to connect to Proxmox[/]");

        if (AnsiConsole.Confirm("Would you like to update Proxmox settings?"))
        {
            await ConfigureProxmoxAsync();
            await configService.SaveConfigAsync(config);
            return await TryConnectAsync();
        }
        return false;
    }

    var version = await proxmoxService.GetVersionAsync();
    AnsiConsole.MarkupLine($"[green]Connected to Proxmox VE {version}[/]");

    // Authenticate with Yandex Disk
    yandexDiskService = host.Services.GetRequiredService<YandexDiskService>();
    var yandexConnected = false;

    await AnsiConsole.Status()
        .StartAsync("Authenticating with Yandex Disk...", async _ =>
        {
            try
            {
                yandexConnected = await yandexDiskService.InitializeAsync(
                    config.Yandex.ClientId,
                    config.Yandex.ClientSecret);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Yandex Disk error: {ex.Message}[/]");
            }
        });

    if (!yandexConnected)
    {
        AnsiConsole.MarkupLine("[red]Failed to connect to Yandex Disk[/]");

        if (AnsiConsole.Confirm("Would you like to update Yandex Disk settings?"))
        {
            await ConfigureYandexAsync();
            await configService.SaveConfigAsync(config);

            // Delete old token to force re-auth
            var tokenStorage = host.Services.GetRequiredService<YandexDiskTokenStorage>();
            tokenStorage.DeleteToken();

            return await TryConnectAsync();
        }
        return false;
    }

    var (totalSpace, usedSpace, _) = await yandexDiskService.GetDiskSpaceAsync();
    AnsiConsole.MarkupLine($"[green]Connected to Yandex Disk[/] ({FormatSize(usedSpace)} used / {FormatSize(totalSpace)} total)");

    // Configure SSH service for remote file access (only needed for remote Proxmox)
    sshService = host.Services.GetRequiredService<SshDownloadService>();
    var isLocal = IsLocalHost(config.Proxmox.Host);

    if (isLocal)
    {
        AnsiConsole.MarkupLine($"[green]Local Proxmox detected[/] - direct file access enabled");
    }
    else if (!string.IsNullOrEmpty(config.Proxmox.SshPassword))
    {
        sshService.Configure(
            config.Proxmox.Host,
            config.Proxmox.SshUsername,
            config.Proxmox.SshPassword,
            config.Proxmox.SshPort);
        AnsiConsole.MarkupLine($"[green]SSH configured[/] for remote file access");
    }
    else
    {
        AnsiConsole.MarkupLine("[yellow]SSH not configured[/] - remote backup files won't be accessible");
        AnsiConsole.MarkupLine("[grey]Configure SSH in Settings to upload existing backups from Proxmox[/]");
    }

    AnsiConsole.WriteLine();

    // Create backup service
    backupService = new BackupService(
        host.Services.GetRequiredService<ILogger<BackupService>>(),
        proxmoxService,
        yandexDiskService,
        host.Services.GetRequiredService<FileSplitter>(),
        host.Services.GetRequiredService<FileHasher>(),
        sshService);

    // Configure Telegram notifications
    telegramService = host.Services.GetRequiredService<TelegramNotificationService>();
    telegramService.Configure(config.Telegram.BotToken, config.Telegram.ChatId);

    if (telegramService.IsConfigured)
    {
        if (!isDaemonMode)
            AnsiConsole.MarkupLine("[green]Telegram notifications enabled[/]");
    }

    // Configure scheduler
    schedulerService = host.Services.GetRequiredService<SchedulerService>();
    schedulerService.Configure(config.Scheduler);
    schedulerService.OnJobExecute += ExecuteScheduledBackupAsync;

    return true;
}

// === Configuration Methods ===

async Task ConfigureProxmoxAsync()
{
    AnsiConsole.MarkupLine("[yellow]Proxmox Configuration[/]");

    config.Proxmox.Host = AnsiConsole.Ask<string>(
        "Enter [green]Proxmox host[/] (e.g., 192.168.1.100):",
        config.Proxmox.Host);

    // Parse port from host if included
    if (config.Proxmox.Host.Contains(':'))
    {
        var parts = config.Proxmox.Host.Split(':');
        config.Proxmox.Host = parts[0];
        if (parts.Length > 1 && int.TryParse(parts[1], out var parsedPort))
        {
            config.Proxmox.Port = parsedPort;
        }
    }

    config.Proxmox.Port = AnsiConsole.Ask("Enter [green]port[/]:", config.Proxmox.Port);

    var authMethod = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select [green]authentication method[/]:")
            .AddChoices("Username/Password", "API Token"));

    if (authMethod == "API Token")
    {
        config.Proxmox.UseApiToken = true;
        config.Proxmox.ApiTokenId = AnsiConsole.Ask<string>(
            "Enter [green]API Token ID[/] (e.g., user@pam!token-name):",
            config.Proxmox.ApiTokenId ?? "");
        config.Proxmox.ApiTokenSecret = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter [green]API Token Secret[/]:")
                .Secret());
    }
    else
    {
        config.Proxmox.UseApiToken = false;
        config.Proxmox.Username = AnsiConsole.Ask<string>(
            "Enter [green]username[/]:",
            config.Proxmox.Username);
        config.Proxmox.Password = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter [green]password[/]:")
                .Secret());
        config.Proxmox.Realm = AnsiConsole.Ask(
            "Enter [green]realm[/]:",
            config.Proxmox.Realm);
    }

    // SSH configuration for remote backup file access
    var isLocalHost = IsLocalHost(config.Proxmox.Host);
    if (isLocalHost)
    {
        AnsiConsole.MarkupLine("[grey]Proxmox is running locally - SSH not required[/]");
        config.Proxmox.SshPassword = null;
    }
    else
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]SSH Configuration[/] (for accessing backup files on remote Proxmox)");

        if (AnsiConsole.Confirm("Configure SSH access?", true))
        {
            config.Proxmox.SshUsername = AnsiConsole.Ask(
                "Enter [green]SSH username[/]:",
                config.Proxmox.SshUsername);
            config.Proxmox.SshPassword = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]SSH password[/]:")
                    .Secret());
            config.Proxmox.SshPort = AnsiConsole.Ask(
                "Enter [green]SSH port[/]:",
                config.Proxmox.SshPort);
        }
    }

    await Task.CompletedTask;
}

static bool IsLocalHost(string host)
{
    if (string.IsNullOrEmpty(host)) return false;

    var lowerHost = host.ToLowerInvariant();
    if (lowerHost is "localhost" or "127.0.0.1" or "::1")
        return true;

    // Check if it matches local machine name
    try
    {
        if (lowerHost == Environment.MachineName.ToLowerInvariant())
            return true;

        // Check if it's a local IP address
        var hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in hostEntry.AddressList)
        {
            if (ip.ToString() == host)
                return true;
        }
    }
    catch
    {
        // Ignore DNS errors
    }

    return false;
}

async Task ConfigureYandexAsync()
{
    AnsiConsole.MarkupLine("\n[yellow]Yandex Disk OAuth Configuration[/]");
    AnsiConsole.MarkupLine("[grey]Create an app at: https://oauth.yandex.ru/[/]");
    AnsiConsole.MarkupLine("[grey]Set callback URL to: http://localhost:8765/callback[/]\n");

    config.Yandex.ClientId = AnsiConsole.Ask<string>(
        "Enter [green]Yandex Client ID[/]:",
        config.Yandex.ClientId);
    config.Yandex.ClientSecret = AnsiConsole.Prompt(
        new TextPrompt<string>("Enter [green]Yandex Client Secret[/]:")
            .Secret());
    config.Yandex.BackupFolder = AnsiConsole.Ask(
        "Enter [green]backup folder on Yandex Disk[/]:",
        config.Yandex.BackupFolder);

    await Task.CompletedTask;
}

async Task ConfigureTelegramAsync()
{
    AnsiConsole.MarkupLine("\n[yellow]Telegram Notification Configuration[/]");
    AnsiConsole.MarkupLine("[grey]Create a bot via @BotFather and get the token[/]");
    AnsiConsole.MarkupLine("[grey]Get your chat ID via @userinfobot[/]\n");

    config.Telegram.BotToken = AnsiConsole.Prompt(
        new TextPrompt<string>("Enter [green]Bot Token[/] (or empty to disable):")
            .AllowEmpty()
            .DefaultValue(config.Telegram.BotToken ?? ""));

    if (!string.IsNullOrEmpty(config.Telegram.BotToken))
    {
        config.Telegram.ChatId = AnsiConsole.Ask(
            "Enter [green]Chat ID[/]:",
            config.Telegram.ChatId ?? "");

        config.Telegram.NotifyOnSuccess = AnsiConsole.Confirm(
            "Notify on [green]successful[/] backups?",
            config.Telegram.NotifyOnSuccess);

        config.Telegram.NotifyOnError = AnsiConsole.Confirm(
            "Notify on [red]failed[/] backups?",
            config.Telegram.NotifyOnError);

        // Test connection
        if (AnsiConsole.Confirm("Test Telegram connection?", true))
        {
            var testService = new TelegramNotificationService(
                host.Services.GetRequiredService<ILogger<TelegramNotificationService>>(),
                new HttpClient());
            testService.Configure(config.Telegram.BotToken, config.Telegram.ChatId);

            if (await testService.TestConnectionAsync())
            {
                AnsiConsole.MarkupLine("[green]Connection successful![/]");
                await testService.SendNotificationAsync("Test message from Proxmox Backup", false);
                AnsiConsole.MarkupLine("[grey]Test message sent[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Connection failed. Check your token and try again.[/]");
            }
        }
    }
    else
    {
        config.Telegram.ChatId = null;
        AnsiConsole.MarkupLine("[grey]Telegram notifications disabled[/]");
    }

    await Task.CompletedTask;
}

async Task ConfigureSchedulerAsync()
{
    AnsiConsole.MarkupLine("\n[yellow]Scheduler Configuration[/]");

    config.Scheduler.Enabled = AnsiConsole.Confirm(
        "Enable scheduler?",
        config.Scheduler.Enabled);

    if (!config.Scheduler.Enabled)
    {
        AnsiConsole.MarkupLine("[grey]Scheduler disabled[/]");
        await Task.CompletedTask;
        return;
    }

    while (true)
    {
        AnsiConsole.WriteLine();

        if (config.Scheduler.Jobs.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]Current Jobs:[/]");
            var table = new Table();
            table.AddColumn("#");
            table.AddColumn("Name");
            table.AddColumn("Schedule");
            table.AddColumn("VMs");
            table.AddColumn("Enabled");

            for (var i = 0; i < config.Scheduler.Jobs.Count; i++)
            {
                var job = config.Scheduler.Jobs[i];
                table.AddRow(
                    (i + 1).ToString(),
                    job.Name,
                    job.CronExpression,
                    string.Join(", ", job.VmIds),
                    job.Enabled ? "[green]Yes[/]" : "[red]No[/]");
            }
            AnsiConsole.Write(table);
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]No jobs configured[/]");
        }

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Scheduler Options")
                .AddChoices("Add new job", "Remove job", "Done"));

        if (action == "Done")
            break;

        if (action == "Add new job")
        {
            var job = new ScheduledBackupJob();

            job.Name = AnsiConsole.Ask<string>("Job [green]name[/]:");

            AnsiConsole.MarkupLine("[grey]Cron format: minute hour day month weekday[/]");
            AnsiConsole.MarkupLine("[grey]Examples: '0 2 * * *' (daily at 2 AM), '0 3 * * 0' (Sundays at 3 AM)[/]");
            job.CronExpression = AnsiConsole.Ask<string>("Cron [green]expression[/]:", "0 2 * * *");

            // Select node
            if (proxmoxService != null)
            {
                var nodes = await proxmoxService.GetNodesAsync();
                job.NodeName = nodes.Count == 1
                    ? nodes[0]
                    : AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select [green]node[/]:")
                            .AddChoices(nodes));

                // Select VMs
                var vms = await proxmoxService.GetAllVirtualMachinesAsync(job.NodeName);
                var vmChoices = vms.Select(v => $"{v.VmId} - {v.Name} ({v.Type})").ToList();

                var selectedVms = AnsiConsole.Prompt(
                    new MultiSelectionPrompt<string>()
                        .Title("Select [green]VMs to backup[/]:")
                        .PageSize(15)
                        .AddChoices(vmChoices));

                job.VmIds = selectedVms
                    .Select(s => int.Parse(s.Split(' ')[0]))
                    .ToList();

                // Select storage
                var storages = await proxmoxService.GetBackupStoragesAsync(job.NodeName);
                var storageChoices = storages.Select(s => s.Storage).ToList();
                job.Storage = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select [green]storage[/]:")
                        .AddChoices(storageChoices));
            }
            else
            {
                job.NodeName = AnsiConsole.Ask<string>("Node [green]name[/]:");
                var vmIdsStr = AnsiConsole.Ask<string>("VM [green]IDs[/] (comma-separated):");
                job.VmIds = vmIdsStr.Split(',').Select(s => int.Parse(s.Trim())).ToList();
                job.Storage = AnsiConsole.Ask<string>("Storage [green]name[/]:");
            }

            job.BackupMode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Backup [green]mode[/]:")
                    .AddChoices("snapshot", "suspend", "stop"));

            job.Compression = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Compression[/]:")
                    .AddChoices("zstd", "gzip", "lzo", "none"));

            job.Enabled = true;
            config.Scheduler.Jobs.Add(job);
            AnsiConsole.MarkupLine($"[green]Job '{job.Name}' added[/]");
        }
        else if (action == "Remove job" && config.Scheduler.Jobs.Count > 0)
        {
            var jobNames = config.Scheduler.Jobs.Select(j => j.Name).ToList();
            var toRemove = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select job to [red]remove[/]:")
                    .AddChoices(jobNames));

            config.Scheduler.Jobs.RemoveAll(j => j.Name == toRemove);
            AnsiConsole.MarkupLine($"[yellow]Job '{toRemove}' removed[/]");
        }
    }

    await Task.CompletedTask;
}

async Task ShowSettingsMenuAsync()
{
    while (true)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Current Settings:[/]");

        var table = new Table();
        table.AddColumn("Setting");
        table.AddColumn("Value");
        table.Border(TableBorder.Rounded);

        table.AddRow("Proxmox Host", $"{config.Proxmox.Host}:{config.Proxmox.Port}");
        table.AddRow("Proxmox Auth", config.Proxmox.UseApiToken ? $"API Token ({config.Proxmox.ApiTokenId})" : $"User ({config.Proxmox.Username}@{config.Proxmox.Realm})");
        table.AddRow("SSH", string.IsNullOrEmpty(config.Proxmox.SshPassword) ? "(not configured)" : $"{config.Proxmox.SshUsername}@{config.Proxmox.Host}:{config.Proxmox.SshPort}");
        table.AddRow("Yandex Client ID", MaskString(config.Yandex.ClientId));
        table.AddRow("Yandex Backup Folder", config.Yandex.BackupFolder);
        table.AddRow("Telegram", telegramService?.IsConfigured == true ? "Configured" : "(not configured)");
        table.AddRow("Scheduler", config.Scheduler.Enabled ? $"{config.Scheduler.Jobs.Count} job(s)" : "Disabled");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Settings Menu")
                .AddChoices(
                    "Change Proxmox settings",
                    "Change Yandex Disk settings",
                    "Configure Telegram notifications",
                    "Configure Scheduler",
                    "Re-authenticate Yandex Disk",
                    "Delete all saved settings",
                    "Back to main menu"));

        switch (choice)
        {
            case "Change Proxmox settings":
                await ConfigureProxmoxAsync();
                await configService.SaveConfigAsync(config);
                AnsiConsole.MarkupLine("[green]Proxmox settings saved. Reconnecting...[/]");

                // Reconnect
                var proxmoxLogger = host.Services.GetRequiredService<ILogger<ProxmoxService>>();
                proxmoxService = new ProxmoxService(proxmoxLogger, config.Proxmox.Host, config.Proxmox.Port);

                if (config.Proxmox.UseApiToken)
                {
                    proxmoxService.LoginWithApiToken(config.Proxmox.ApiTokenId!, config.Proxmox.ApiTokenSecret!);
                }
                else
                {
                    await proxmoxService.LoginAsync(config.Proxmox.Username, config.Proxmox.Password, config.Proxmox.Realm);
                }

                backupService = new BackupService(
                    host.Services.GetRequiredService<ILogger<BackupService>>(),
                    proxmoxService,
                    yandexDiskService!,
                    host.Services.GetRequiredService<FileSplitter>(),
                    host.Services.GetRequiredService<FileHasher>());
                break;

            case "Change Yandex Disk settings":
                await ConfigureYandexAsync();
                await configService.SaveConfigAsync(config);
                AnsiConsole.MarkupLine("[green]Yandex settings saved.[/]");
                break;

            case "Configure Telegram notifications":
                await ConfigureTelegramAsync();
                await configService.SaveConfigAsync(config);
                telegramService?.Configure(config.Telegram.BotToken, config.Telegram.ChatId);
                AnsiConsole.MarkupLine("[green]Telegram settings saved.[/]");
                break;

            case "Configure Scheduler":
                await ConfigureSchedulerAsync();
                await configService.SaveConfigAsync(config);
                schedulerService?.Configure(config.Scheduler);
                AnsiConsole.MarkupLine("[green]Scheduler settings saved.[/]");
                break;

            case "Re-authenticate Yandex Disk":
                var tokenStorage = host.Services.GetRequiredService<YandexDiskTokenStorage>();
                tokenStorage.DeleteToken();
                AnsiConsole.MarkupLine("[yellow]Token deleted. Re-authenticating...[/]");
                await yandexDiskService!.InitializeAsync(config.Yandex.ClientId, config.Yandex.ClientSecret);
                AnsiConsole.MarkupLine("[green]Re-authenticated successfully![/]");
                break;

            case "Delete all saved settings":
                if (AnsiConsole.Confirm("[red]Are you sure you want to delete all saved settings?[/]", false))
                {
                    configService.DeleteConfig();
                    var yandexTokenStorage = host.Services.GetRequiredService<YandexDiskTokenStorage>();
                    yandexTokenStorage.DeleteToken();
                    AnsiConsole.MarkupLine("[yellow]All settings deleted. Please restart the application.[/]");
                    Environment.Exit(0);
                }
                break;

            case "Back to main menu":
                return;
        }
    }
}

// === Backup Methods ===

async Task CreateAndUploadBackupAsync()
{
    if (proxmoxService == null || backupService == null)
    {
        AnsiConsole.MarkupLine("[red]Not connected. Please check settings.[/]");
        return;
    }

    var nodes = await proxmoxService.GetNodesAsync();
    if (nodes.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]No nodes found[/]");
        return;
    }

    var selectedNode = nodes.Count == 1
        ? nodes[0]
        : AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [green]node[/]:")
                .AddChoices(nodes));

    var vms = await proxmoxService.GetAllVirtualMachinesAsync(selectedNode);
    if (vms.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]No VMs or containers found[/]");
        return;
    }

    var vmChoices = vms.Select(v => $"[[{v.Type.ToUpper()}]] {v.VmId} - {v.Name} ({v.Status})").ToList();
    var selectedVmStr = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select [green]VM/Container[/] to backup:")
            .PageSize(15)
            .AddChoices(vmChoices));

    var selectedVm = vms[vmChoices.IndexOf(selectedVmStr)];

    AnsiConsole.MarkupLine("[grey]Fetching available storages...[/]");

    // Show all storages for debugging
    var allStorages = await proxmoxService.GetAllStoragesAsync(selectedNode);
    if (allStorages.Count > 0)
    {
        AnsiConsole.MarkupLine($"[grey]All storages on node ({allStorages.Count} total):[/]");
        foreach (var s in allStorages)
        {
            var hasBackup = s.Content.Contains("backup");
            var color = hasBackup ? "green" : "grey";
            AnsiConsole.MarkupLine($"  [{color}]{s.Storage.EscapeMarkup()}[/] - content: {s.Content.EscapeMarkup()}, enabled: {s.Enabled}, active: {s.Active}");
        }
        AnsiConsole.WriteLine();
    }

    var storages = await proxmoxService.GetBackupStoragesAsync(selectedNode);
    if (storages.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]No backup storage found[/]");
        AnsiConsole.MarkupLine("[yellow]Tip: Make sure your storage has 'backup' content type enabled in Proxmox.[/]");
        AnsiConsole.MarkupLine("[yellow]Check Datacenter -> Storage -> Edit storage -> Content -> enable 'VZDump backup file'[/]");
        return;
    }

    var storageChoices = storages.Select(s => $"{s.Storage} ({FormatSize(s.Avail)} free)").ToList();
    var selectedStorageStr = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select [green]storage[/] for backup:")
            .AddChoices(storageChoices));

    var selectedStorage = storages[storageChoices.IndexOf(selectedStorageStr)];

    var backupMode = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select [green]backup mode[/]:")
            .AddChoices("snapshot", "suspend", "stop"));

    var compress = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select [green]compression[/]:")
            .AddChoices("zstd", "gzip", "lzo", "none"));

    AnsiConsole.WriteLine();
    var summaryTable = new Table();
    summaryTable.AddColumn("Setting");
    summaryTable.AddColumn("Value");
    summaryTable.AddRow("Node", selectedNode);
    summaryTable.AddRow("VM/CT", $"{selectedVm.Name} ({selectedVm.VmId})");
    summaryTable.AddRow("Type", selectedVm.Type.ToUpper());
    summaryTable.AddRow("Storage", selectedStorage.Storage);
    summaryTable.AddRow("Mode", backupMode);
    summaryTable.AddRow("Compression", compress);
    summaryTable.AddRow("Yandex Folder", config.Yandex.BackupFolder);
    AnsiConsole.Write(summaryTable);

    if (!AnsiConsole.Confirm("Start backup?"))
        return;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[blue]Starting backup process...[/]");
    AnsiConsole.WriteLine();

    var logLines = new List<string>();

    await AnsiConsole.Progress()
        .AutoClear(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("[green]Backing up...[/]");

            var progress = new Progress<BackupProgress>(p =>
            {
                task.Description = $"[green]{p.Message}[/]";
                task.Value = p.PercentComplete;

                // Show Proxmox log messages
                if (p.Message.StartsWith("INFO:") || p.Message.StartsWith("ERROR:") ||
                    p.Message.Contains("vzdump") || p.Message.Contains("backup") ||
                    p.Message.Contains("creating") || p.Message.Contains("starting") ||
                    p.Message.Contains("finished") || p.Message.Contains("error"))
                {
                    if (!logLines.Contains(p.Message))
                    {
                        logLines.Add(p.Message);
                    }
                }
            });

            var success = await backupService.StreamingBackupAndUploadAsync(
                selectedNode,
                selectedVm.VmId,
                selectedStorage.Storage,
                config.Yandex.BackupFolder,
                backupMode,
                compress,
                progress);

            task.Description = success
                ? "[green]Backup completed successfully![/]"
                : "[red]Backup failed![/]";
            task.Value = 100;
        });

    // Show collected log lines
    if (logLines.Count > 0)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Proxmox task log:[/]");
        foreach (var line in logLines)
        {
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(line)}[/]");
            else if (line.Contains("warning", StringComparison.OrdinalIgnoreCase))
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(line)}[/]");
            else
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(line)}[/]");
        }
    }
}

async Task UploadExistingBackupAsync()
{
    if (proxmoxService == null || backupService == null)
    {
        AnsiConsole.MarkupLine("[red]Not connected. Please check settings.[/]");
        return;
    }

    string localPath;
    int vmId;
    string vmName;
    string vmType;
    string nodeName;
    string? selectedStorageName = null;

    // Get nodes
    var nodes = await proxmoxService.GetNodesAsync();
    if (nodes.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]No nodes found[/]");
        return;
    }

    var selectedNode = nodes.Count == 1
        ? nodes[0]
        : AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [green]node[/]:")
                .AddChoices(nodes));

    nodeName = selectedNode;

    // Get all backup storages
    var storages = await proxmoxService.GetBackupStoragesAsync(selectedNode);

    // Get VM info for display
    var allVms = await proxmoxService.GetAllVirtualMachinesAsync(selectedNode);
    var vmLookup = allVms.ToDictionary(v => v.VmId, v => v);

    // Collect all backups from all storages
    var allBackupsWithStorage = new List<(ProxmoxBackupInfo Backup, string StorageName)>();

    await AnsiConsole.Status()
        .StartAsync("Loading backups from all storages...", async _ =>
        {
            foreach (var storage in storages)
            {
                var backups = await proxmoxService.GetBackupsAsync(selectedNode, storage.Storage);
                foreach (var backup in backups)
                {
                    allBackupsWithStorage.Add((backup, storage.Storage));
                }
            }
        });

    // Build choices list: all backups + manual option
    const string manualOption = ">>> Enter path manually <<<";
    var choices = new List<string> { manualOption };

    // Sort backups by date (newest first)
    var sortedBackups = allBackupsWithStorage
        .OrderByDescending(b => b.Backup.CTime)
        .ToList();

    foreach (var (backup, storageName) in sortedBackups)
    {
        var backupDate = DateTimeOffset.FromUnixTimeSeconds(backup.CTime).LocalDateTime;
        var vmInfo = vmLookup.TryGetValue(backup.VmId, out var vm) ? vm.Name : $"VM {backup.VmId}";
        var typeFromVolId = backup.VolId.Contains("qemu") ? "QEMU" : (backup.VolId.Contains("lxc") ? "LXC" : "???");
        // Double brackets to escape markup interpretation in Spectre.Console
        choices.Add($"[[{storageName}]] {vmInfo} ({backup.VmId}) [[{typeFromVolId}]] - {backupDate:yyyy-MM-dd HH:mm} - {FormatSize(backup.Size)}");
    }

    if (choices.Count == 1)
    {
        AnsiConsole.MarkupLine("[yellow]No backups found in Proxmox storages[/]");
    }

    var selectedChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select [green]backup[/] to upload:")
            .PageSize(20)
            .HighlightStyle(Style.Parse("green"))
            .AddChoices(choices));

    if (selectedChoice == manualOption)
    {
        // Manual path entry
        localPath = AnsiConsole.Ask<string>("Enter [green]path to backup file[/]:");

        if (!File.Exists(localPath))
        {
            AnsiConsole.MarkupLine("[red]File not found[/]");
            return;
        }

        vmId = AnsiConsole.Ask<int>("Enter [green]VM ID[/]:");
        vmName = AnsiConsole.Ask<string>("Enter [green]VM name[/]:");
        vmType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [green]VM type[/]:")
                .AddChoices("qemu", "lxc"));
    }
    else
    {
        // Selected backup from Proxmox
        var selectedIndex = choices.IndexOf(selectedChoice) - 1; // -1 for manual option
        var (selectedBackup, storageName) = sortedBackups[selectedIndex];
        selectedStorageName = storageName;

        AnsiConsole.MarkupLine($"[grey]Storage: {Markup.Escape(storageName)}[/]");
        AnsiConsole.MarkupLine($"[grey]VolId: {Markup.Escape(selectedBackup.VolId)}[/]");

        // Get backup file path - prefer pvesm for accurate path resolution
        string? backupFilePath = null;

        // Try local pvesm first (if running on Proxmox server)
        foreach (var pvesmPath in new[] { "pvesm", "/usr/sbin/pvesm" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pvesmPath,
                    Arguments = $"path {selectedBackup.VolId}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        backupFilePath = output.Trim();
                        AnsiConsole.MarkupLine($"[grey]Path (via local pvesm): {Markup.Escape(backupFilePath)}[/]");
                        break;
                    }
                    else if (!string.IsNullOrWhiteSpace(error))
                    {
                        AnsiConsole.MarkupLine($"[grey]pvesm error: {Markup.Escape(error.Trim())}[/]");
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[grey]pvesm ({pvesmPath}): {Markup.Escape(ex.Message)}[/]");
            }

            if (!string.IsNullOrEmpty(backupFilePath))
                break;
        }

        // Try pvesm via SSH if local didn't work
        if (string.IsNullOrEmpty(backupFilePath) && sshService != null && sshService.IsConfigured)
        {
            backupFilePath = sshService.GetProxmoxVolumePath(selectedBackup.VolId);
            if (!string.IsNullOrEmpty(backupFilePath))
            {
                AnsiConsole.MarkupLine($"[grey]Path (via SSH pvesm): {Markup.Escape(backupFilePath)}[/]");
            }
        }

        // Fallback to API-based path resolution
        if (string.IsNullOrEmpty(backupFilePath))
        {
            backupFilePath = await proxmoxService.GetBackupFilePathAsync(selectedNode, storageName, selectedBackup.VolId);
            if (!string.IsNullOrEmpty(backupFilePath))
            {
                AnsiConsole.MarkupLine($"[grey]Path (via API): {Markup.Escape(backupFilePath)}[/]");
            }
        }

        if (string.IsNullOrEmpty(backupFilePath))
        {
            AnsiConsole.MarkupLine("[red]Could not determine backup file path[/]");
            return;
        }

        localPath = backupFilePath;
        vmId = selectedBackup.VmId;
        vmName = vmLookup.TryGetValue(selectedBackup.VmId, out var vmDetails) ? vmDetails.Name : $"VM{selectedBackup.VmId}";
        vmType = selectedBackup.VolId.Contains("lxc") ? "lxc" : "qemu";
    }

    // Check file accessibility
    // If SSH is configured - Proxmox is remote, use SSH
    // If SSH is not configured - Proxmox is local, use local file access
    long fileSize;
    bool isRemoteFile;

    if (sshService != null && sshService.IsConfigured)
    {
        // SSH configured = remote Proxmox, use SSH
        isRemoteFile = true;
        var (success, error) = sshService.TryGetFileInfo(localPath, out var remoteFileInfo);
        if (!success || !remoteFileInfo.HasValue)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error ?? "File not found")}[/]");
            AnsiConsole.MarkupLine($"[grey]Path: {Markup.Escape(localPath)}[/]");
            return;
        }

        fileSize = remoteFileInfo.Value.Size;
        AnsiConsole.MarkupLine($"[green]Remote file (SSH)[/] - Size: [yellow]{FormatSize(fileSize)}[/]");

        if (fileSize > 49L * 1024 * 1024 * 1024)
        {
            AnsiConsole.MarkupLine("[yellow]File exceeds 49 GB, will be split into chunks[/]");
        }
    }
    else
    {
        // SSH not configured = local Proxmox, use local file access
        isRemoteFile = false;
        if (!File.Exists(localPath))
        {
            AnsiConsole.MarkupLine("[red]File not found[/]");
            AnsiConsole.MarkupLine($"[grey]Path: {Markup.Escape(localPath)}[/]");
            AnsiConsole.MarkupLine("[yellow]If Proxmox is on remote server, configure SSH in Settings -> Change Proxmox settings[/]");
            return;
        }

        var fileInfo = new FileInfo(localPath);
        fileSize = fileInfo.Length;
        AnsiConsole.MarkupLine($"[green]Local file[/] - Size: [yellow]{FormatSize(fileSize)}[/]");

        if (fileSize > 49L * 1024 * 1024 * 1024)
        {
            AnsiConsole.MarkupLine("[yellow]File exceeds 49 GB, will be split into chunks[/]");
        }
    }

    var proxmoxVersion = await proxmoxService.GetVersionAsync() ?? "unknown";

    if (!AnsiConsole.Confirm("Start upload?"))
        return;

    await AnsiConsole.Progress()
        .AutoClear(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("[green]Uploading...[/]");

            var progress = new Progress<BackupProgress>(p =>
            {
                task.Description = $"[green]{p.Message}[/]";
                task.Value = p.PercentComplete;
            });

            var success = await backupService.UploadExistingBackupAsync(
                localPath,
                config.Yandex.BackupFolder,
                vmId,
                vmName,
                vmType,
                nodeName,
                proxmoxVersion,
                isRemoteFile,
                progress);

            task.Description = success
                ? "[green]Upload completed successfully![/]"
                : "[red]Upload failed![/]";
            task.Value = 100;
        });
}

async Task ListBackupsAsync()
{
    if (backupService == null)
    {
        AnsiConsole.MarkupLine("[red]Not connected. Please check settings.[/]");
        return;
    }

    await AnsiConsole.Status()
        .StartAsync("Loading backups...", async _ =>
        {
            var backups = await backupService.ListBackupsAsync(config.Yandex.BackupFolder);

            if (backups.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No backups found[/]");
                return;
            }

            var table = new Table();
            table.AddColumn("VM");
            table.AddColumn("Type");
            table.AddColumn("Size");
            table.AddColumn("Chunks");
            table.AddColumn("Created");
            table.AddColumn("Node");

            foreach (var backup in backups.OrderByDescending(b => b.CreatedAt))
            {
                table.AddRow(
                    $"{backup.VmName} ({backup.VmId})",
                    backup.VmType.ToUpper(),
                    FormatSize(backup.OriginalFileSize),
                    backup.TotalChunks.ToString(),
                    backup.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    backup.NodeName);
            }

            AnsiConsole.Write(table);
        });
}

async Task DownloadAndRestoreAsync()
{
    if (backupService == null || yandexDiskService == null)
    {
        AnsiConsole.MarkupLine("[red]Not connected. Please check settings.[/]");
        return;
    }

    var backups = await backupService.ListBackupsAsync(config.Yandex.BackupFolder);

    if (backups.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No backups found[/]");
        return;
    }

    var choices = backups
        .OrderByDescending(b => b.CreatedAt)
        .Select(b => $"{b.VmName} ({b.VmId}) - {b.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} - {FormatSize(b.OriginalFileSize)}")
        .ToList();

    var selected = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select [green]backup to restore[/]:")
            .PageSize(15)
            .AddChoices(choices));

    var selectedBackup = backups.OrderByDescending(b => b.CreatedAt).ToList()[choices.IndexOf(selected)];

    var outputPath = AnsiConsole.Ask<string>("Enter [green]output directory[/]:", "/tmp/restore");

    var folders = await yandexDiskService.ListFolderAsync(config.Yandex.BackupFolder);
    var backupFolder = folders.FirstOrDefault(f =>
        f.Name.Contains(selectedBackup.VmName) &&
        f.Name.Contains(selectedBackup.VmId.ToString()));

    if (backupFolder == null)
    {
        AnsiConsole.MarkupLine("[red]Backup folder not found on Yandex Disk[/]");
        return;
    }

    await AnsiConsole.Progress()
        .AutoClear(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("[green]Restoring...[/]");

            var progress = new Progress<BackupProgress>(p =>
            {
                task.Description = $"[green]{p.Message}[/]";
                task.Value = p.PercentComplete;
            });

            var restoredPath = await backupService.DownloadAndReassembleAsync(
                backupFolder.Path,
                outputPath,
                progress);

            task.Description = restoredPath != null
                ? $"[green]Restored to: {restoredPath}[/]"
                : "[red]Restore failed![/]";
            task.Value = 100;
        });
}

// === Helper Methods ===

static string FormatSize(long bytes)
{
    string[] sizes = ["B", "KB", "MB", "GB", "TB"];
    var order = 0;
    double size = bytes;

    while (size >= 1024 && order < sizes.Length - 1)
    {
        order++;
        size /= 1024;
    }

    return $"{size:0.##} {sizes[order]}";
}

static string MaskString(string input)
{
    if (string.IsNullOrEmpty(input)) return "(not set)";
    if (input.Length <= 8) return "****";
    return input[..4] + "****" + input[^4..];
}

static void ShowHelp()
{
    Console.WriteLine("Proxmox Backup to Yandex Disk");
    Console.WriteLine();
    Console.WriteLine("Usage: ProxmoxBackupToYandexDisk.Console [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --help, -h     Show this help");
    Console.WriteLine("  --daemon, -d   Run as background service with scheduler");
    Console.WriteLine("  --test         Run integration tests");
    Console.WriteLine();
    Console.WriteLine("Modes:");
    Console.WriteLine("  Interactive    Default mode with menu-driven interface");
    Console.WriteLine("  Daemon         Background service mode for scheduled backups");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("  - Configuration is saved automatically and encrypted");
    Console.WriteLine("  - Config location: ~/.proxmox-backup/");
    Console.WriteLine("  - For daemon mode, configure scheduler in interactive mode first");
    Console.WriteLine();
    Console.WriteLine("Systemd service:");
    Console.WriteLine("  Copy proxmox-backup.service to /etc/systemd/system/");
    Console.WriteLine("  systemctl enable proxmox-backup");
    Console.WriteLine("  systemctl start proxmox-backup");
}

// === Daemon Mode ===

async Task RunDaemonModeAsync()
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Starting Proxmox Backup daemon mode...");
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Proxmox Backup daemon started");

    if (schedulerService == null || !config.Scheduler.Enabled || config.Scheduler.Jobs.Count == 0)
    {
        logger.LogWarning("No scheduled jobs configured. Configure jobs in interactive mode first.");
        Console.WriteLine("WARNING: No scheduled jobs configured. Exiting.");
        return;
    }

    // Start scheduler
    schedulerService.Start();

    // Show next runs
    var nextRuns = schedulerService.GetNextRuns().ToList();
    foreach (var (jobName, nextRun) in nextRuns)
    {
        logger.LogInformation("Job '{JobName}' next run: {NextRun}", jobName, nextRun);
        Console.WriteLine($"  Job '{jobName}' next run: {nextRun:yyyy-MM-dd HH:mm:ss}");
    }

    // Wait for termination signal
    var tcs = new TaskCompletionSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        logger.LogInformation("Received termination signal");
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Shutting down...");
        tcs.TrySetResult();
    };

    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        tcs.TrySetResult();
    };

    await tcs.Task;

    // Graceful shutdown
    await schedulerService.StopAsync();
    logger.LogInformation("Proxmox Backup daemon stopped");
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Daemon stopped");
}

async Task<bool> ExecuteScheduledBackupAsync(ScheduledBackupJob job, CancellationToken cancellationToken)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Executing scheduled backup job: {JobName}", job.Name);

    if (proxmoxService == null || backupService == null || telegramService == null)
    {
        logger.LogError("Services not initialized");
        return false;
    }

    var allSuccess = true;
    var startTime = DateTime.Now;

    foreach (var vmId in job.VmIds)
    {
        try
        {
            logger.LogInformation("Starting backup for VM {VmId}", vmId);

            // Get VM info for notifications
            var allVms = await proxmoxService.GetAllVirtualMachinesAsync(job.NodeName);
            var vm = allVms.FirstOrDefault(v => v.VmId == vmId);
            var vmName = vm?.Name ?? $"VM{vmId}";

            var vmStartTime = DateTime.Now;

            var success = await backupService.StreamingBackupAndUploadAsync(
                job.NodeName,
                vmId,
                job.Storage,
                config.Yandex.BackupFolder,
                job.BackupMode,
                job.Compression,
                new Progress<BackupProgress>(p =>
                    logger.LogDebug("{VmId}: {Message} ({Percent}%)", vmId, p.Message, p.PercentComplete)),
                cancellationToken);

            var duration = DateTime.Now - vmStartTime;

            if (success)
            {
                logger.LogInformation("Backup completed for VM {VmId} in {Duration}", vmId, duration);

                if (config.Telegram.NotifyOnSuccess)
                {
                    await telegramService.SendBackupSuccessAsync(vmName, vmId, job.NodeName, duration);
                }
            }
            else
            {
                logger.LogError("Backup failed for VM {VmId}", vmId);
                allSuccess = false;

                if (config.Telegram.NotifyOnError)
                {
                    await telegramService.SendBackupErrorAsync(vmName, vmId, job.NodeName, "Backup or upload failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Backup cancelled for VM {VmId}", vmId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during backup of VM {VmId}", vmId);
            allSuccess = false;

            if (config.Telegram.NotifyOnError)
            {
                await telegramService.SendBackupErrorAsync($"VM{vmId}", vmId, job.NodeName, ex.Message);
            }
        }
    }

    var totalDuration = DateTime.Now - startTime;
    logger.LogInformation("Job '{JobName}' completed in {Duration}, success: {Success}",
        job.Name, totalDuration, allSuccess);

    return allSuccess;
}
