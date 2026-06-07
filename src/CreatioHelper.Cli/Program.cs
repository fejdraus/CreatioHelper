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
            Console.WriteLine($"creatio-helper-cli {ver}");
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
                "lic" => await RunLicAsync(provider, settings, cli, output, cts.Token),
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

        var site = ResolveSiteContext(settings, output);
        if (site == null || string.IsNullOrWhiteSpace(site.Path))
        {
            return 2;
        }

        if (!Directory.Exists(site.Path))
        {
            output.WriteLine($"[ERROR] Site path does not exist: {site.Path}");
            return 2;
        }

        if (site.Version == new Version())
        {
            try
            {
                site.Version = AppVersionHelper.GetAppVersion(site.Path);
            }
            catch
            {
                site.Version = new Version();
            }
        }

        bool effectiveIisMode = settings.IsIisMode || !string.IsNullOrWhiteSpace(site.Name);
        var orchestrator = provider.GetRequiredService<IDeploymentOrchestrator>();
        var options = new DeploymentOptions
        {
            SitePath = site.Path,
            SiteVersion = site.Version,
            IsIisMode = effectiveIisMode,
            IisSiteName = site.Name,
            IisPoolName = site.PoolName,
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
            SkipServerRestart = cli.HasFlag("no-iis-restart"),
            IisPoolOnly = site.IsVirtualApp,
            QuickInstall = cli.HasFlag("quick-install")
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
        var site = ResolveSiteContext(settings, output);
        if (site == null || string.IsNullOrWhiteSpace(site.Path))
        {
            return 2;
        }

        var factory = provider.GetRequiredService<IRedisManagerFactory>();
        var manager = factory.Create(site.Path);
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
            output.WriteLine("[ERROR] Use: creatio-helper-cli iis start|stop|restart");
            return 2;
        }

        var orchestrator = provider.GetRequiredService<IDeploymentOrchestrator>();
        IEnumerable<ServerInfo> servers = settings.ServerList;

        if (!servers.Any())
        {
            var site = ResolveSiteContext(settings, output);
            var resolvedName = site?.Name ?? settings.SelectedIisSiteName;
            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                servers = new[]
                {
                    new ServerInfo
                    {
                        Name = new CreatioHelper.Domain.ValueObjects.ServerName(Environment.MachineName),
                        SiteName = site?.IsVirtualApp == true ? string.Empty : (resolvedName ?? string.Empty),
                        PoolName = site?.PoolName ?? string.Empty
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

    private static async Task<int> RunLicAsync(IServiceProvider provider, AppSettings settings, CliArgs cli, IOutputWriter output, CancellationToken ct)
    {
        ApplyOverrides(settings, cli);

        var action = cli.SubCommand?.ToLowerInvariant();
        if (action != "load" && action != "request")
        {
            output.WriteLine("[ERROR] Use: creatio-helper-cli lic load|request");
            return 2;
        }

        var site = ResolveSiteContext(settings, output);
        if (site == null || string.IsNullOrWhiteSpace(site.Path))
        {
            return 2;
        }

        var preparer = provider.GetRequiredService<IWorkspacePreparer>();

        if (action == "load")
        {
            var licFile = cli.Get("lic-file");
            if (string.IsNullOrWhiteSpace(licFile))
            {
                output.WriteLine("[ERROR] Provide --lic-file <path>");
                return 2;
            }
            if (!File.Exists(licFile))
            {
                output.WriteLine($"[ERROR] License file not found: {licFile}");
                return 2;
            }
            int code = preparer.LoadLicResponse(site.Path, licFile);
            return code == 0 ? 0 : 1;
        }
        else
        {
            var destination = cli.Get("destination");
            var customerId = cli.Get("customer-id");
            if (string.IsNullOrWhiteSpace(destination))
            {
                output.WriteLine("[ERROR] Provide --destination <path>");
                return 2;
            }
            if (string.IsNullOrWhiteSpace(customerId))
            {
                output.WriteLine("[ERROR] Provide --customer-id <id>");
                return 2;
            }
            var fileName = cli.Get("file-name") ?? string.Empty;
            int code = preparer.SaveLicenseRequest(site.Path, destination, customerId, fileName);
            return code == 0 ? 0 : 1;
        }
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

        var serverArgs = cli.GetAll("server");
        if (serverArgs.Length > 0)
        {
            s.ServerList.Clear();
            foreach (var arg in serverArgs)
            {
                var server = ParseServerArg(arg);
                if (server != null)
                {
                    s.ServerList.Add(server);
                }
            }
            s.IsServerPanelVisible = true;
            s.EnableFileCopySynchronization = true;
        }

        if (cli.Get("sync-folders") is { Length: > 0 } syncFolders)
        {
            var paths = syncFolders
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            foreach (var server in s.ServerList)
            {
                server.FileCopyFolderPaths = paths;
            }
        }

        if (cli.Get("sync-exclude") is { Length: > 0 } syncExclude)
        {
            var patterns = syncExclude
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            foreach (var server in s.ServerList)
            {
                server.FileCopyExcludePatterns = patterns;
            }
        }
    }

    private static ServerInfo? ParseServerArg(string arg)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in arg.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                dict[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }

        if (!dict.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var server = new ServerInfo { Name = name };
        if (dict.TryGetValue("host", out var host)) server.SshHost = host;
        if (dict.TryGetValue("port", out var portStr) && int.TryParse(portStr, out var port)) server.SshPort = port;
        if (dict.TryGetValue("user", out var user)) server.SshUser = user;
        if (dict.TryGetValue("pass", out var pass)) server.SshPassword = pass;
        if (dict.TryGetValue("key", out var key)) server.SshKeyPath = key;
        if (dict.TryGetValue("path", out var path)) server.NetworkPath = path;
        if (dict.TryGetValue("service", out var service)) server.ServiceName = service;
        if (dict.TryGetValue("sudo", out var sudo) && sudo == "true") server.SshSudoEnabled = true;
        return server;
    }

    private static IisSiteInfo? ResolveSiteContext(AppSettings s, IOutputWriter output)
    {
        if (s.IsIisMode && !string.IsNullOrWhiteSpace(s.SelectedIisSiteName))
        {
            if (!OperatingSystem.IsWindows())
            {
                output.WriteLine("[ERROR] IIS mode requires Windows. Provide --site <path> instead.");
                return null;
            }

            try
            {
                return ResolveIisSiteByName(s.SelectedIisSiteName!);
            }
            catch (Exception ex)
            {
                output.WriteLine($"[ERROR] Cannot resolve IIS site '{s.SelectedIisSiteName}': {ex.Message}");
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(s.SitePath) && OperatingSystem.IsWindows())
        {
            try
            {
                var match = FindIisSiteByPath(s.SitePath);
                if (match != null)
                {
                    return match;
                }
            }
            catch
            {
            }
        }

        return new IisSiteInfo { Path = s.SitePath ?? string.Empty };
    }

    private static IisSiteInfo? FindIisSiteByPath(string sitePath)
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
                        return new IisSiteInfo
                        {
                            Name = app.Path != "/" ? $"{site.Name}{app.Path}" : site.Name,
                            Path = sitePath,
                            PoolName = app.ApplicationPoolName ?? string.Empty
                        };
                    }
                }
            }
        }
        return null;
    }

    private static IisSiteInfo ResolveIisSiteByName(string input)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("IIS is only supported on Windows.");
        }

        var slashIdx = input.IndexOf('/');
        string siteName = slashIdx > 0 ? input[..slashIdx] : input;
        string appPath = slashIdx > 0 ? input[slashIdx..] : "/";

        using var manager = new Microsoft.Web.Administration.ServerManager();
        var site = manager.Sites[siteName] ?? throw new InvalidOperationException($"IIS site '{siteName}' not found.");
        var app = site.Applications[appPath] ?? throw new InvalidOperationException($"Application '{appPath}' not found in IIS site '{siteName}'.");

        var vdir = app.VirtualDirectories["/"];
        var physical = vdir?.PhysicalPath ?? string.Empty;
        Version version = new();
        if (!string.IsNullOrWhiteSpace(physical) && Directory.Exists(physical))
        {
            try { version = AppVersionHelper.GetAppVersion(physical); } catch { }
        }

        return new IisSiteInfo
        {
            Name = input,
            Path = physical,
            PoolName = app.ApplicationPoolName ?? string.Empty,
            Version = version
        };
    }

    private static CompileMode ParseCompileMode(string? value)
    {
        return (value?.ToLowerInvariant()) switch
        {
            "full" => CompileMode.Full,
            "incremental" => CompileMode.Incremental,
            "none" => CompileMode.None,
            _ => CompileMode.Default
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
        Console.WriteLine("creatio-helper-cli — Creatio deployment CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  creatio-helper-cli [options]                            Run deployment (only steps with non-empty values)");
        Console.WriteLine("  creatio-helper-cli redis-clear [options]                Clear Redis cache");
        Console.WriteLine("  creatio-helper-cli iis start|stop|restart [options]     Manage IIS pools/sites");
        Console.WriteLine("  creatio-helper-cli lic load [options]                   Load license response file into Creatio");
        Console.WriteLine("  creatio-helper-cli lic request [options]                Save license request file from Creatio");
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
        Console.WriteLine("  --compile incremental|full        Compile strategy (default: full if packages, incremental otherwise)");
        Console.WriteLine("  --sync none|files|syncthing  Sync mode for multi-server");
        Console.WriteLine("  --sync-folders \"A,B\"          Relative folder paths to sync (e.g. \"Terrasoft.Configuration,Terrasoft.WebApp/conf\")");
        Console.WriteLine("  --server \"name=X,...\"         Add target server (repeatable; replaces ServerList from settings)");
        Console.WriteLine("                               Keys: name, host, port, user, pass, key, path, service, sudo");
        Console.WriteLine("                               sudo=true  upload via /tmp then sudo mv (for Linux targets with PermitRootLogin no)");
        Console.WriteLine("  --no-redis-clear             Skip Redis cache clear (useful when attaching IDE to Creatio)");
        Console.WriteLine("  --no-iis-restart             Skip IIS stop/start during compile (keeps process alive for IDE attach)");
        Console.WriteLine("  --quick-install              Skip RebuildWorkspace and BuildConfiguration after package install (faster, like clio)");
        Console.WriteLine("  --no-color                   Disable ANSI colors");
        Console.WriteLine("  --quiet                      Only print [ERROR] lines");
        Console.WriteLine();
        Console.WriteLine("lic load options:");
        Console.WriteLine("  --lic-file <path>            Path to the license response file (.lic)");
        Console.WriteLine();
        Console.WriteLine("lic request options:");
        Console.WriteLine("  --destination <path>         Directory to save the license request file");
        Console.WriteLine("  --customer-id <id>           Customer ID for the license request");
        Console.WriteLine("  --file-name <name>           Output file name (optional)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  creatio-helper-cli --settings .\\deploy.json");
        Console.WriteLine("  creatio-helper-cli --site C:\\Site --compile full");
        Console.WriteLine("  creatio-helper-cli --site C:\\Site --packages-path C:\\Pkgs");
        Console.WriteLine("  creatio-helper-cli --site C:\\Site --packages-path C:\\Pkgs --quick-install");
        Console.WriteLine("  creatio-helper-cli --iis-site \"Default Web Site\"");
        Console.WriteLine("  creatio-helper-cli --iis-site \"Default Web Site/creatio\" --packages-path C:\\Pkgs");
        Console.WriteLine("  creatio-helper-cli iis restart --settings .\\deploy.json");
        Console.WriteLine("  creatio-helper-cli lic load --site C:\\Site --lic-file C:\\license.lic");
        Console.WriteLine("  creatio-helper-cli lic request --site C:\\Site --destination C:\\Out --customer-id 12345");
        Console.WriteLine();
        Console.WriteLine("  # Sync to Linux server where PermitRootLogin is disabled (passwordless sudo required):");
        Console.WriteLine("  creatio-helper-cli --site C:\\Site --sync files --compile none");
        Console.WriteLine("    --server \"name=prod,host=10.0.0.1,user=croot,key=/home/user/.ssh/id_rsa,path=/var/www/creatio,sudo=true\"");
        Console.WriteLine("  # sudoers entry (restrictive): croot ALL=(ALL) NOPASSWD: /usr/bin/mv,/usr/bin/chown,/usr/bin/touch,/usr/bin/mkdir,/usr/bin/rm");
    }
}
