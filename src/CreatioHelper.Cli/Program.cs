using System.Text;
using CreatioHelper.Application.Extensions;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Operations;
using CreatioHelper.Cli;
using ConsoleOutputWriter = CreatioHelper.Cli.Services.ConsoleOutputWriter;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Extensions;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Workspace;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Logging;
using CreatioHelper.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
return await CliEntryPoint.RunAsync(args);

internal static class CliEntryPoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || args[0] == "help")
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        if (args[0] == "--version")
        {
            var ver = typeof(CliEntryPoint).Assembly.GetName().Version?.ToString() ?? "unknown";
            Console.WriteLine($"creatio-cli {ver}");
            return 0;
        }

        var cli = CliArgs.Parse(args);
        bool noColor = cli.HasFlag("no-color");
        bool quiet = cli.HasFlag("quiet");

        OutputWriterHandlers.WriteAction = _ => { };
        OutputWriterHandlers.ClearAction = () => { };

        var services = new ServiceCollection();
        services.AddSingleton<IOutputWriter>(new ConsoleOutputWriter(useColor: !noColor, quiet: quiet));
        services.AddInfrastructureServices();
        services.AddApplication();

        services.Remove(services.First(d => d.ServiceType == typeof(IOutputWriter)));
        services.AddSingleton<IOutputWriter>(new ConsoleOutputWriter(useColor: !noColor, quiet: quiet));

        await using var provider = services.BuildServiceProvider();
        var output = provider.GetRequiredService<IOutputWriter>();

        AppSettings settings = LoadSettings(cli, output);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return (cli.Command?.ToLowerInvariant()) switch
            {
                "redis-clear" => await RunRedisClearAsync(provider, settings, cli, output, cts.Token),
                "iis" => await RunIisAsync(provider, settings, cli, output, cts.Token),
                _ => await RunDeployAsync(provider, settings, cli, output, cts.Token)
            };
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("[INFO] Operation was cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            output.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }
    }

    private static AppSettings LoadSettings(CliArgs cli, IOutputWriter output)
    {
        var settingsPath = cli.Get("settings");
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return new AppSettings();
        }

        if (!File.Exists(settingsPath))
        {
            output.WriteLine($"[ERROR] Settings file not found: {settingsPath}");
            return new AppSettings();
        }

        return AppSettingsService.Load(settingsPath);
    }

    private static async Task<int> RunDeployAsync(IServiceProvider provider, AppSettings settings, CliArgs cli, IOutputWriter output, CancellationToken ct)
    {
        ApplyOverrides(settings, cli);

        if (string.IsNullOrWhiteSpace(settings.SitePath) && string.IsNullOrWhiteSpace(settings.SelectedIisSiteName))
        {
            output.WriteLine("[ERROR] No site is configured. Provide --site <path>, --iis-site <name>, or --settings <path>.");
            return 2;
        }

        var (resolvedSitePath, resolvedVersion, resolvedPool, resolvedSiteName) = ResolveSiteContext(settings, output);
        if (string.IsNullOrWhiteSpace(resolvedSitePath))
        {
            return 2;
        }

        if (!Directory.Exists(resolvedSitePath))
        {
            output.WriteLine($"[ERROR] Site path does not exist: {resolvedSitePath}");
            return 2;
        }

        if (resolvedVersion == null)
        {
            try
            {
                resolvedVersion = AppVersionHelper.GetAppVersion(resolvedSitePath);
            }
            catch
            {
                resolvedVersion = new Version();
            }
        }

        bool effectiveIisMode = settings.IsIisMode || !string.IsNullOrWhiteSpace(resolvedSiteName);
        var orchestrator = provider.GetRequiredService<IDeploymentOrchestrator>();
        var options = new DeploymentOptions
        {
            SitePath = resolvedSitePath,
            SiteVersion = resolvedVersion,
            IsIisMode = effectiveIisMode,
            IisSiteName = resolvedSiteName ?? (settings.IsIisMode ? settings.SelectedIisSiteName : null),
            IisPoolName = resolvedPool,
            ServiceName = settings.ServiceName,
            PackagesPath = settings.PackagesPath,
            PackagesToDeleteBefore = settings.PackagesToDeleteBefore,
            PackagesToDeleteAfter = settings.PackagesToDeleteAfter,
            PrevalidateBeforeInstall = settings.PrevalidateBeforeInstall,
            ResetUnlockedPackageFlags = settings.ResetUnlockedPackageFlags,
            Compile = ParseCompileMode(cli.Get("compile")),
            Sync = ResolveSyncMode(settings, cli),
            Servers = settings.ServerList.ToArray(),
            HasRemoteServers = settings.IsServerPanelVisible && settings.ServerList.Count > 0,
            SkipRedisClear = cli.HasFlag("no-redis-clear"),
            SkipServerRestart = cli.HasFlag("no-iis-restart")
        };

        var result = await orchestrator.RunAsync(options, NullDeploymentUiCallbacks.Instance, ct).ConfigureAwait(false);
        if (result.Cancelled)
        {
            return 130;
        }
        return result.Success ? 0 : 1;
    }

    private static async Task<int> RunRedisClearAsync(IServiceProvider provider, AppSettings settings, CliArgs cli, IOutputWriter output, CancellationToken ct)
    {
        ApplyOverrides(settings, cli);
        var (resolvedSitePath, _, _, _) = ResolveSiteContext(settings, output);
        if (string.IsNullOrWhiteSpace(resolvedSitePath))
        {
            return 2;
        }

        var factory = provider.GetRequiredService<IRedisManagerFactory>();
        var manager = factory.Create(resolvedSitePath);
        bool status = manager.CheckStatus();
        if (!status)
        {
            output.WriteLine("[ERROR] Redis is unavailable.");
            return 1;
        }
        manager.Clear();
        output.WriteLine("[OK] Redis cache cleared.");
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> RunIisAsync(IServiceProvider provider, AppSettings settings, CliArgs cli, IOutputWriter output, CancellationToken ct)
    {
        ApplyOverrides(settings, cli);

        var action = cli.SubCommand?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action) || (action != "start" && action != "stop" && action != "restart"))
        {
            output.WriteLine("[ERROR] Use: creatio-cli iis start|stop|restart");
            return 2;
        }

        var orchestrator = provider.GetRequiredService<IDeploymentOrchestrator>();
        IEnumerable<ServerInfo> servers = settings.ServerList;

        if (!servers.Any())
        {
            var (_, _, pool, siteName) = ResolveSiteContext(settings, output);
            var resolvedName = siteName ?? settings.SelectedIisSiteName;
            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                servers = new[]
                {
                    new ServerInfo
                    {
                        Name = new CreatioHelper.Domain.ValueObjects.ServerName(Environment.MachineName),
                        SiteName = resolvedName ?? string.Empty,
                        PoolName = pool ?? string.Empty
                    }
                };
            }
        }

        switch (action)
        {
            case "start":
                await orchestrator.StartAllIisAsync(servers, ct).ConfigureAwait(false);
                break;
            case "stop":
                await orchestrator.StopAllIisAsync(servers, ct).ConfigureAwait(false);
                break;
            case "restart":
                await orchestrator.RestartAllIisAsync(servers, null, ct).ConfigureAwait(false);
                break;
        }

        return 0;
    }

    private static void ApplyOverrides(AppSettings s, CliArgs cli)
    {
        if (cli.Get("site") is { Length: > 0 } sitePath)
        {
            s.SitePath = sitePath;
            s.IsIisMode = false;
        }
        if (cli.Get("iis-site") is { Length: > 0 } iisSite)
        {
            s.SelectedIisSiteName = iisSite;
            s.IsIisMode = true;
        }
        if (cli.Get("service-name") is { } svc) s.ServiceName = svc;
        if (cli.Get("packages-path") is { } pkg) s.PackagesPath = pkg;
        if (cli.Get("delete-before") is { } db) s.PackagesToDeleteBefore = db;
        if (cli.Get("delete-after") is { } da) s.PackagesToDeleteAfter = da;
        if (cli.Get("prevalidate") is { } prev && bool.TryParse(prev, out var pb)) s.PrevalidateBeforeInstall = pb;
        if (cli.Get("reset-unlocked-flags") is { } ruf && bool.TryParse(ruf, out var rufv))
        {
            s.ResetUnlockedPackageFlags = rufv;
        }
        else if (cli.HasFlag("reset-unlocked-flags"))
        {
            s.ResetUnlockedPackageFlags = true;
        }
    }

    private static (string? path, Version? version, string? pool, string? siteName) ResolveSiteContext(AppSettings s, IOutputWriter output)
    {
        if (s.IsIisMode && !string.IsNullOrWhiteSpace(s.SelectedIisSiteName))
        {
            if (!OperatingSystem.IsWindows())
            {
                output.WriteLine("[ERROR] IIS mode requires Windows. Provide --site <path> instead.");
                return (null, null, null, null);
            }

            try
            {
                var (p, v, pool) = ResolveIisSiteByName(s.SelectedIisSiteName!);
                return (p, v, pool, s.SelectedIisSiteName);
            }
            catch (Exception ex)
            {
                output.WriteLine($"[ERROR] Cannot resolve IIS site '{s.SelectedIisSiteName}': {ex.Message}");
                return (null, null, null, null);
            }
        }

        if (!string.IsNullOrWhiteSpace(s.SitePath) && OperatingSystem.IsWindows())
        {
            try
            {
                var match = FindIisSiteByPath(s.SitePath);
                if (match.HasValue)
                {
                    return (s.SitePath, null, match.Value.pool, match.Value.siteName);
                }
            }
            catch
            {
                // Fall through to plain folder mode
            }
        }

        return (s.SitePath, null, null, null);
    }

    private static (string siteName, string? pool)? FindIisSiteByPath(string sitePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sitePath));
        using var manager = new Microsoft.Web.Administration.ServerManager();
        foreach (var site in manager.Sites)
        {
            foreach (var app in site.Applications)
            {
                foreach (var vdir in app.VirtualDirectories)
                {
                    var phys = vdir.PhysicalPath;
                    if (string.IsNullOrWhiteSpace(phys))
                    {
                        continue;
                    }

                    var normPhys = Path.TrimEndingDirectorySeparator(Path.GetFullPath(phys));
                    if (string.Equals(normPhys, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return (site.Name, app.ApplicationPoolName);
                    }
                }
            }
        }
        return null;
    }

    private static (string? path, Version? version, string? pool) ResolveIisSiteByName(string siteName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, null, null);
        }

        using var manager = new Microsoft.Web.Administration.ServerManager();
        var site = manager.Sites[siteName];
        if (site == null)
        {
            throw new InvalidOperationException($"IIS site '{siteName}' not found.");
        }

        var app = site.Applications["/"];
        var vdir = app?.VirtualDirectories["/"];
        var physical = vdir?.PhysicalPath;
        var pool = app?.ApplicationPoolName;
        Version? version = null;
        if (!string.IsNullOrWhiteSpace(physical) && Directory.Exists(physical))
        {
            try
            {
                version = AppVersionHelper.GetAppVersion(physical);
            }
            catch
            {
                version = null;
            }
        }
        return (physical, version, pool);
    }

    private static CompileMode ParseCompileMode(string? value)
    {
        return (value?.ToLowerInvariant()) switch
        {
            "full" => CompileMode.Full,
            "incremental" => CompileMode.Incremental,
            _ => CompileMode.Auto
        };
    }

    private static SyncMode ResolveSyncMode(AppSettings settings, CliArgs cli)
    {
        var raw = cli.Get("sync")?.ToLowerInvariant();
        if (raw != null)
        {
            return raw switch
            {
                "files" => SyncMode.FileCopy,
                "syncthing" => SyncMode.Syncthing,
                _ => SyncMode.None
            };
        }
        if (settings.UseSyncthingForSync) return SyncMode.Syncthing;
        if (settings.EnableFileCopySynchronization) return SyncMode.FileCopy;
        return SyncMode.None;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("creatio-cli — Creatio deployment CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  creatio-cli [options]                            Run deployment (only steps with non-empty values)");
        Console.WriteLine("  creatio-cli redis-clear [options]                Clear Redis cache");
        Console.WriteLine("  creatio-cli iis start|stop|restart [options]     Manage IIS pools/sites");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --settings <path>            Load AppSettings from JSON (same format as Desktop settings.json)");
        Console.WriteLine("  --site <path>                Filesystem site path (overrides settings)");
        Console.WriteLine("  --iis-site <name>            IIS site name (Windows only)");
        Console.WriteLine("  --service-name <name>        Windows service name (Folder mode)");
        Console.WriteLine("  --packages-path <path>       Install packages from path");
        Console.WriteLine("  --delete-before \"A,B\"        Delete packages before installation");
        Console.WriteLine("  --delete-after  \"A,B\"        Delete packages after installation");
        Console.WriteLine("  --prevalidate true|false     Prevalidate before install");
        Console.WriteLine("  --reset-unlocked-flags       Reset IsLocked/IsChanged on unlocked packages (locked are reset by default)");
        Console.WriteLine("  --compile auto|incremental|full   Compile strategy (default: auto)");
        Console.WriteLine("  --sync none|files|syncthing  Sync mode for multi-server");
        Console.WriteLine("  --no-redis-clear             Skip Redis cache clear (useful when attaching IDE to Creatio)");
        Console.WriteLine("  --no-iis-restart             Skip IIS stop/start during compile (keeps process alive for IDE attach)");
        Console.WriteLine("  --no-color                   Disable ANSI colors");
        Console.WriteLine("  --quiet                      Only print [ERROR] lines");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  creatio-cli --settings .\\deploy.json");
        Console.WriteLine("  creatio-cli --site C:\\Site --compile full");
        Console.WriteLine("  creatio-cli --site C:\\Site --packages-path C:\\Pkgs");
        Console.WriteLine("  creatio-cli iis restart --settings .\\deploy.json");
    }
}
