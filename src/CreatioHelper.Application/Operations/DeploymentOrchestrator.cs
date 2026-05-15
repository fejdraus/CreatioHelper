using System.Collections.Concurrent;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Application.Operations;

public class DeploymentOrchestrator : IDeploymentOrchestrator
{
    private readonly IOutputWriter _output;
    private readonly IIisManager _iisManager;
    private readonly ISiteSynchronizer _siteSynchronizer;
    private readonly IWorkspacePreparer _workspacePreparer;
    private readonly ICustomDescriptorUpdater _customDescriptorUpdater;
    private readonly IRedisManagerFactory _redisManagerFactory;
    private readonly IMetricsService _metricsService;
    private readonly IServerStatusService _statusService;

    public DeploymentOrchestrator(
        IOutputWriter output,
        IIisManager iisManager,
        ISiteSynchronizer siteSynchronizer,
        IWorkspacePreparer workspacePreparer,
        ICustomDescriptorUpdater customDescriptorUpdater,
        IRedisManagerFactory redisManagerFactory,
        IMetricsService metricsService,
        IServerStatusService statusService)
    {
        _output = output;
        _iisManager = iisManager;
        _siteSynchronizer = siteSynchronizer;
        _workspacePreparer = workspacePreparer;
        _customDescriptorUpdater = customDescriptorUpdater;
        _redisManagerFactory = redisManagerFactory;
        _metricsService = metricsService;
        _statusService = statusService;
    }

    public async Task<DeploymentResult> RunAsync(
        DeploymentOptions options,
        IDeploymentUiCallbacks? ui = null,
        CancellationToken cancellationToken = default)
    {
        ui ??= NullDeploymentUiCallbacks.Instance;
        ui.OnStopButtonEnabledChanged(false);
        _output.Clear();

        if (string.IsNullOrWhiteSpace(options.SitePath))
        {
            _output.WriteLine("The path to the site is not indicated.");
            return DeploymentResult.Fail("Site path is not specified.");
        }

        string sitePath = options.SitePath;
        bool quartzIsActiveOriginal = true;
        bool hadError = false;

        ui.OnBusyChanged(true);
        ui.OnStartButtonText("In process...");
        ui.OnServerControlsEnabledChanged(false);

        try
        {
            await Task.Run(async () =>
            {
                string packagesPath = options.PackagesPath ?? "";
                string packagesBefore = options.PackagesToDeleteBefore?.Trim() ?? "";
                string packagesAfter = options.PackagesToDeleteAfter?.Trim() ?? "";
                var serverList = options.Servers.ToArray();
                var preparer = _workspacePreparer;

                bool hasPackageOps = !string.IsNullOrWhiteSpace(packagesPath) ||
                                     !string.IsNullOrWhiteSpace(packagesBefore) ||
                                     !string.IsNullOrWhiteSpace(packagesAfter);

                bool fullRebuild = options.Compile switch
                {
                    CompileMode.Full => true,
                    CompileMode.Incremental => false,
                    CompileMode.Auto => true,
                    _ => true
                };

                if (options.Compile == CompileMode.Incremental && hasPackageOps)
                {
                    _output.WriteLine("[INFO] Package operations require full rebuild — Compile mode overridden to Compile All.");
                    fullRebuild = true;
                }
                else if (options.Compile == CompileMode.Auto)
                {
                    fullRebuild = hasPackageOps;
                    if (!fullRebuild)
                    {
                        fullRebuild = false;
                    }
                }

                _output.WriteLine("Prepare WorkspaceConsole ...");
                preparer.Prepare(sitePath, out quartzIsActiveOriginal);

                if (cancellationToken.IsCancellationRequested)
                {
                    _metricsService.IncrementCounter("deployment_cancelled");
                    return;
                }

                string nestedPath = Path.Combine(sitePath, "Terrasoft.WebApp", "bin", "Terrasoft.Common.dll");
                var poolName = options.IsIisMode ? options.IisPoolName : null;
                var siteName = options.IsIisMode ? options.IisSiteName : null;
                var appVersion = options.SiteVersion;

                if (appVersion == null || appVersion < new Version(7, 12, 0, 0))
                {
                    _output.WriteLine("[ERROR] Creatio application not found.");
                    hadError = true;
                    return;
                }

                var localServerInfo = new ServerInfo
                {
                    Name = new ServerName(Environment.MachineName),
                    PoolName = poolName ?? string.Empty,
                    SiteName = siteName ?? string.Empty,
                    ServiceName = options.ServiceName ?? string.Empty
                };
                var manager = _iisManager;

                ui.OnStopButtonEnabledChanged(true);

                bool schemaRebuildPerformed = false;
                bool serversAlreadyStopped = false;

                if (!string.IsNullOrWhiteSpace(packagesBefore) && appVersion >= Constants.MinimumVersionForDeletePackages)
                {
                    bool willStopLaterForPackages = !string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath);
                    if (!willStopLaterForPackages)
                    {
                        _output.WriteLine("Stopping local server before deleting packages...");
                        await PerformIisOperationsAsync(manager, localServerInfo, nestedPath, options.IsIisMode, options.ServiceName, options.HasRemoteServers, cancellationToken).ConfigureAwait(false);
                    }

                    _output.WriteLine("Deleting packages BEFORE installation...");

                    bool success = false;
                    _metricsService.Measure("packages_delete_before", () =>
                    {
                        success = ExecutePreparerAction(() => preparer.DeletePackages(sitePath, packagesBefore), "[ERROR] Deleting packages failed.", cancellationToken);
                        if (!success) return;

                        _customDescriptorUpdater.RemoveDependencies(sitePath, packagesBefore);

                        success = ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                        if (!success) return;

                        success = ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                    });

                    if (!success)
                    {
                        _output.WriteLine("[ERROR] Package deletion BEFORE installation failed. Stopping execution.");
                        _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = "packages_delete_before_failed" });
                        hadError = true;
                        return;
                    }

                    _metricsService.IncrementCounter("packages_deleted_before");
                }

                if (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath))
                {
                    if (options.PrevalidateBeforeInstall)
                    {
                        _output.WriteLine("Running pre-validation before installation...");
                        bool prevalidateSuccess = ExecutePreparerAction(
                            () => preparer.PrevalidateInstallFromRepository(sitePath, packagesPath),
                            "[ERROR] Pre-validation failed. Installation aborted.",
                            cancellationToken);
                        if (!prevalidateSuccess)
                        {
                            _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = "prevalidation_failed" });
                            hadError = true;
                            return;
                        }
                        _output.WriteLine("[OK] Pre-validation passed.");
                    }

                    _output.WriteLine("Stopping ALL servers before package installation...");
                    await StopAllServersBeforeInstallation(manager, localServerInfo, nestedPath, serverList, options.HasRemoteServers, cancellationToken).ConfigureAwait(false);
                    serversAlreadyStopped = true;
                    _output.WriteLine("Start installation packages...");
                    bool success = false;
                    _metricsService.Measure("package_install", () =>
                    {
                        success = ExecutePreparerAction(() => preparer.InstallFromRepository(sitePath, packagesPath), "[ERROR] Failed to install packages.", cancellationToken);
                        if (!success) return;

                        success = ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                        if (!success) return;

                        success = ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                    });

                    if (!success)
                    {
                        _output.WriteLine("[ERROR] Package installation failed. Stopping execution.");
                        _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = "package_install_failed" });
                        hadError = true;
                        return;
                    }

                    _metricsService.IncrementCounter("packages_installed_count");
                }

                if (!string.IsNullOrWhiteSpace(packagesAfter) && appVersion >= Constants.MinimumVersionForDeletePackages)
                {
                    bool wasStoppedEarlier = !string.IsNullOrWhiteSpace(packagesBefore) ||
                                            (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath));
                    if (!wasStoppedEarlier)
                    {
                        _output.WriteLine("Stopping local server before deleting packages...");
                        await PerformIisOperationsAsync(manager, localServerInfo, nestedPath, options.IsIisMode, options.ServiceName, options.HasRemoteServers, cancellationToken).ConfigureAwait(false);
                    }

                    _output.WriteLine("Deleting packages AFTER installation...");

                    bool success = false;
                    _metricsService.Measure("packages_delete_after", () =>
                    {
                        success = ExecutePreparerAction(() => preparer.DeletePackages(sitePath, packagesAfter), "[ERROR] Deleting packages failed.", cancellationToken);
                        if (!success) return;

                        _customDescriptorUpdater.RemoveDependencies(sitePath, packagesAfter);

                        success = ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                        if (!success) return;

                        success = ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                    });

                    if (!success)
                    {
                        _output.WriteLine("[ERROR] Package deletion AFTER installation failed. Stopping execution.");
                        _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = "packages_delete_after_failed" });
                        hadError = true;
                        return;
                    }

                    _metricsService.IncrementCounter("packages_deleted_after");
                }

                if (string.IsNullOrWhiteSpace(packagesPath) &&
                    string.IsNullOrWhiteSpace(packagesBefore) &&
                    string.IsNullOrWhiteSpace(packagesAfter))
                {
                    schemaRebuildPerformed = true;

                    if (options.SkipServerRestart)
                    {
                        _output.WriteLine("[INFO] Skipping IIS stop/start (Skip IIS Restart enabled).");
                    }
                    else if (options.HasRemoteServers && serverList.Length > 0)
                    {
                        _output.WriteLine("Stopping ALL servers (local + remote) before schema rebuild...");
                        await StopAllServersBeforeInstallation(manager, localServerInfo, nestedPath, serverList, options.HasRemoteServers, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _output.WriteLine("Stopping local server before schema rebuild...");
                        await PerformIisOperationsAsync(manager, localServerInfo, nestedPath, options.IsIisMode, options.ServiceName, options.HasRemoteServers, cancellationToken).ConfigureAwait(false);
                    }

                    ui.OnStopButtonEnabledChanged(true);

                    _output.WriteLine(fullRebuild
                        ? "Performing schema regeneration and full compilation (Compile All)..."
                        : "Performing incremental compilation (Compile)...");
                    bool success = false;
                    _metricsService.Measure("schema_operations", () =>
                    {
                        if (fullRebuild)
                        {
                            success = ExecutePreparerAction(() => preparer.RegenerateSchemaSources(sitePath), "[ERROR] Failed to regenerate schema sources.", cancellationToken);
                            if (!success) return;

                            success = ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                            if (!success) return;

                            success = ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath, force: true), "[ERROR] Building configuration failed.", cancellationToken);
                        }
                        else
                        {
                            success = ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath, force: false), "[ERROR] Building configuration failed.", cancellationToken);
                        }
                    });

                    if (!success)
                    {
                        _output.WriteLine("[ERROR] Schema operations failed. Stopping execution.");
                        _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = "schema_operations_failed" });
                        hadError = true;
                        return;
                    }

                    _output.WriteLine(fullRebuild
                        ? "[OK] Schema regeneration and compilation completed successfully."
                        : "[OK] Incremental compilation completed successfully.");
                }

                if (!options.SkipRedisClear)
                {
                    var redisManager = _redisManagerFactory.Create(sitePath);
                    var redisStatus = redisManager.CheckStatus();
                    if (redisStatus)
                    {
                        redisManager.Clear();
                        _output.WriteLine("[OK] Redis cache cleared.");
                    }
                }

                bool usedSyncthingOrchestration = false;
                bool usedManagePoolsOnly = false;

                if (!schemaRebuildPerformed && OperatingSystem.IsWindows() && serverList.Length > 0 && options.HasRemoteServers)
                {
                    ui.OnStopButtonEnabledChanged(false);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        if (options.Sync == SyncMode.Syncthing && options.SyncthingMonitor != null)
                        {
                            await PerformSyncthingOrchestrationAsync(manager, localServerInfo, nestedPath, serverList, options.SyncthingMonitor, cancellationToken).ConfigureAwait(false);
                            usedSyncthingOrchestration = true;
                        }
                        else if (options.Sync == SyncMode.FileCopy)
                        {
                            var syncStatus = await _metricsService.MeasureAsync("server_sync", async () => await _siteSynchronizer.SynchronizeAsync(sitePath, serverList.ToList(), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

                            if (syncStatus)
                            {
                                _output.WriteLine("[OK] All servers are successfully synchronized.");
                                _metricsService.IncrementCounter("successful_deployments_count");
                                _metricsService.SetGauge("servers_synchronized_count", serverList.Length);
                            }
                            else
                            {
                                _output.WriteLine("[ERROR] Failed to synchronize servers.");
                                _metricsService.IncrementCounter("failed_deployments_count");
                                hadError = true;
                                return;
                            }
                        }
                        else
                        {
                            await ManageServerPoolsOnlyAsync(new List<ServerInfo>(serverList), options.HasRemoteServers, serversAlreadyStopped, cancellationToken).ConfigureAwait(false);
                            usedManagePoolsOnly = true;
                        }
                    }
                    ui.OnStopButtonEnabledChanged(true);
                }

                ui.OnStopButtonEnabledChanged(false);

                if (!usedSyncthingOrchestration && !usedManagePoolsOnly)
                {
                    bool skipStart = options.SkipServerRestart && !hasPackageOps;
                    if (!skipStart)
                    {
                        bool hasRemoteServers = options.HasRemoteServers && serverList.Length > 0;

                        await _metricsService.MeasureAsync("startup_operations", async () =>
                        {
                            if ((schemaRebuildPerformed || serversAlreadyStopped) && hasRemoteServers)
                            {
                                await StartAllServersAfterRebuild(manager, localServerInfo, nestedPath, serverList, options.HasRemoteServers, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, options.HasRemoteServers, cancellationToken).ConfigureAwait(false);
                            }
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine("[INFO] Operation was cancelled.");
            _metricsService.IncrementCounter("deployment_cancelled");
            ui.OnStopButtonEnabledChanged(true);
            return DeploymentResult.CancelledResult();
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] {ex.Message}");
            _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = ex.GetType().Name });
            ui.OnStopButtonEnabledChanged(true);
            return DeploymentResult.Fail(ex.Message);
        }
        finally
        {
            ui.OnBusyChanged(false);
            ui.OnStartButtonText("Start");
            ui.OnServerControlsEnabledChanged(true);
            ui.OnStopButtonEnabledChanged(true);
            if (!quartzIsActiveOriginal)
            {
                string config = Directory.Exists(Path.Combine(sitePath, "Terrasoft.WebApp"))
                    ? Path.Combine(sitePath, "Web.config")
                    : Path.Combine(sitePath, "Terrasoft.WebHost.dll.config");
                _workspacePreparer.UpdateOutConfig(config, quartzIsActiveOriginal);
            }
        }

        return hadError ? DeploymentResult.Fail() : DeploymentResult.Ok();
    }

    private bool ExecutePreparerAction(Func<int> action, string errorMessage, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }

        int result = action();
        if (result != 0)
        {
            _output.WriteLine(errorMessage);
            return false;
        }
        return true;
    }

    private async Task RefreshServerStatusIfNeededAsync(ServerInfo server, bool isServerPanelVisible, CancellationToken cancellationToken)
    {
        if (isServerPanelVisible && OperatingSystem.IsWindows())
        {
            await Task.Delay(500, cancellationToken);
            await _statusService.RefreshServerStatusOnUIThreadAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(bool poolStopped, bool siteStopped, bool serviceStopped)> StopServerAsync(
        ServerInfo server,
        bool isLocal,
        string? nestedPath,
        bool shouldRefreshUI,
        CancellationToken cancellationToken)
    {
        bool poolStopped = false;
        bool siteStopped = false;
        bool serviceStopped = false;

        string serverName = isLocal ? Environment.MachineName : (server.Name ?? "");
        string serverLabel = isLocal ? "Local" : (server.Name ?? "Remote");

        bool isIisMode = !isLocal || (nestedPath != null && File.Exists(nestedPath));

        if (OperatingSystem.IsWindows() && isIisMode)
        {
            if (!string.IsNullOrWhiteSpace(server.PoolName))
            {
                var result = await _iisManager.StopAppPoolAsync(serverName, server.PoolName, cancellationToken);
                poolStopped = result.IsSuccess;
                if (poolStopped)
                {
                    _output.WriteLine(isLocal ? $"[INFO] {serverLabel} Pool stopped." : $"[INFO] Stopped app pool '{server.PoolName}' on {server.Name}");
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to stop app pool '{server.PoolName}' on {serverLabel}: {result.ErrorMessage}");
                }
            }

            if (!string.IsNullOrWhiteSpace(server.SiteName))
            {
                var result = await _iisManager.StopWebsiteAsync(serverName, server.SiteName, cancellationToken);
                siteStopped = result.IsSuccess;
                if (siteStopped)
                {
                    _output.WriteLine(isLocal ? $"[INFO] {serverLabel} Website stopped." : $"[INFO] Stopped website '{server.SiteName}' on {server.Name}");
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to stop website '{server.SiteName}' on {serverLabel}: {result.ErrorMessage}");
                }
            }
        }

        if (!isIisMode && !string.IsNullOrWhiteSpace(server.ServiceName))
        {
            var result = await _iisManager.StopServiceAsync(serverName, server.ServiceName, cancellationToken);
            serviceStopped = result.IsSuccess;
            if (serviceStopped)
            {
                _output.WriteLine($"[INFO] Main Service stopped.");
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to stop service: {result.ErrorMessage}");
            }
        }

        await RefreshServerStatusIfNeededAsync(server, shouldRefreshUI, cancellationToken);

        return (poolStopped, siteStopped, serviceStopped);
    }

    private async Task StartServerAsync(
        ServerInfo server,
        bool isLocal,
        string? nestedPath,
        bool shouldRefreshUI,
        bool poolWasStopped,
        bool siteWasStopped,
        bool serviceWasStopped,
        CancellationToken cancellationToken)
    {
        string serverName = isLocal ? Environment.MachineName : (server.Name ?? "");
        string serverLabel = isLocal ? "Main" : (server.Name ?? "Remote");

        bool isIisMode = !isLocal || (nestedPath != null && File.Exists(nestedPath));

        if (OperatingSystem.IsWindows() && isIisMode)
        {
            if (poolWasStopped && !string.IsNullOrWhiteSpace(server.PoolName))
            {
                var result = await _iisManager.StartAppPoolAsync(serverName, server.PoolName, cancellationToken);
                if (result.IsSuccess)
                {
                    _output.WriteLine(isLocal ? $"[INFO] {serverLabel} Pool is running." : $"[INFO] Started app pool '{server.PoolName}' on {server.Name}");
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to start app pool '{server.PoolName}' on {serverLabel}: {result.ErrorMessage}");
                }
            }

            if (siteWasStopped && !string.IsNullOrWhiteSpace(server.SiteName))
            {
                var result = await _iisManager.StartWebsiteAsync(serverName, server.SiteName, cancellationToken);
                if (result.IsSuccess)
                {
                    _output.WriteLine(isLocal ? $"[INFO] {serverLabel} Website is running." : $"[INFO] Started website '{server.SiteName}' on {server.Name}");
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to start website '{server.SiteName}' on {serverLabel}: {result.ErrorMessage}");
                }
            }
        }

        if (serviceWasStopped && !string.IsNullOrWhiteSpace(server.ServiceName))
        {
            var result = await _iisManager.StartServiceAsync(serverName, server.ServiceName, cancellationToken);
            if (result.IsSuccess)
            {
                _output.WriteLine($"[INFO] Main Service is running.");
            }
            else
            {
                _output.WriteLine($"[WARNING] Failed to start main service.");
            }
        }

        await RefreshServerStatusIfNeededAsync(server, shouldRefreshUI, cancellationToken);
    }

    private async Task PerformIisOperationsAsync(IIisManager manager, ServerInfo localServerInfo, string nestedPath, bool isIisMode, string? serviceName, bool shouldRefreshUI, CancellationToken cancellationToken)
    {
        if (!File.Exists(nestedPath) && !string.IsNullOrWhiteSpace(serviceName))
        {
            localServerInfo.ServiceName = serviceName;
        }

        await StopServerAsync(localServerInfo, isLocal: true, nestedPath, shouldRefreshUI, cancellationToken);
    }

    private async Task PerformStartupOperationsAsync(IIisManager manager, ServerInfo localServerInfo, string nestedPath, bool shouldRefreshUI, CancellationToken cancellationToken)
    {
        await StartServerAsync(localServerInfo, isLocal: true, nestedPath, shouldRefreshUI,
            poolWasStopped: true, siteWasStopped: true, serviceWasStopped: true, cancellationToken);
    }

    private async Task ManageServerPoolsOnlyAsync(List<ServerInfo> targetServers, bool shouldRefreshUI, bool serversAlreadyStopped, CancellationToken cancellationToken)
    {
        _output.WriteLine("[INFO] Managing server pools without file synchronization...");

        var stopResults = new Dictionary<ServerInfo, (bool poolStopped, bool siteStopped, bool serviceStopped)>();

        if (!serversAlreadyStopped)
        {
            foreach (var server in targetServers)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _output.WriteLine("[INFO] Pool management was cancelled.");
                    return;
                }

                var result = await StopServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI, cancellationToken);
                stopResults[server] = result;
            }

            _output.WriteLine("[INFO] Verifying services are stopped...");
            await Task.Delay(3000, cancellationToken);
        }
        else
        {
            foreach (var server in targetServers)
            {
                stopResults[server] = (true, true, false);
            }
            _output.WriteLine("[INFO] Servers already stopped from package operations.");
        }

        foreach (var server in targetServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _output.WriteLine("[INFO] Pool management restart was cancelled.");
                return;
            }

            var (poolWasStopped, siteWasStopped, serviceWasStopped) = stopResults[server];
            await StartServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI,
                poolWasStopped, siteWasStopped, serviceWasStopped, cancellationToken);
        }

        _output.WriteLine("[OK] Server pool management completed.");
        _metricsService.IncrementCounter("pool_management_completed");
    }

    private async Task StopAllServersBeforeInstallation(IIisManager manager, ServerInfo localServerInfo, string nestedPath, ServerInfo[] remoteServers, bool shouldRefreshUI, CancellationToken cancellationToken)
    {
        await StopServerAsync(localServerInfo, isLocal: true, nestedPath, shouldRefreshUI, cancellationToken);

        foreach (var server in remoteServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _output.WriteLine("[INFO] Stopping servers was cancelled.");
                return;
            }

            await Task.Delay(1000, cancellationToken);
            await StopServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI, cancellationToken);
        }

        _output.WriteLine("[INFO] Waiting for ALL services to fully stop...");
        await Task.Delay(3000, cancellationToken);

        _output.WriteLine("[OK] All servers (local + remote) stopped successfully.");
    }

    private async Task StartAllServersAfterRebuild(IIisManager manager, ServerInfo localServerInfo, string nestedPath, ServerInfo[] remoteServers, bool shouldRefreshUI, CancellationToken cancellationToken)
    {
        _output.WriteLine("Starting ALL servers (local + remote) after schema rebuild...");

        await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, shouldRefreshUI, cancellationToken).ConfigureAwait(false);

        foreach (var server in remoteServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _output.WriteLine("[INFO] Starting servers was cancelled.");
                return;
            }

            await Task.Delay(1000, cancellationToken);
            await StartServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI,
                poolWasStopped: true, siteWasStopped: true, serviceWasStopped: false, cancellationToken);
        }

        _output.WriteLine("[OK] All servers (local + remote) started successfully.");
    }

    public async Task StartAllIisAsync(IEnumerable<ServerInfo> servers, CancellationToken cancellationToken = default)
    {
        if (!_iisManager.IsSupported())
        {
            _output.WriteLine("[ERROR] IIS is not supported on this platform");
            return;
        }

        var serverList = servers.Where(s => !string.IsNullOrEmpty(s.PoolName) || !string.IsNullOrEmpty(s.SiteName)).ToList();

        if (!serverList.Any())
        {
            _output.WriteLine("[INFO] No servers have IIS sites or pools configured");
            return;
        }

        try
        {
            _output.WriteLine($"[INFO] Starting {serverList.Count} IIS servers...");

            var tasks = serverList.Select(async server =>
            {
                await StartServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI: true,
                    poolWasStopped: true, siteWasStopped: true, serviceWasStopped: false, cancellationToken);
            });

            await Task.WhenAll(tasks);
            _output.WriteLine("[OK] All IIS servers started.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to start IIS: {ex.Message}");
        }
    }

    public async Task StopAllIisAsync(IEnumerable<ServerInfo> servers, CancellationToken cancellationToken = default)
    {
        if (!_iisManager.IsSupported())
        {
            _output.WriteLine("[ERROR] IIS is not supported on this platform");
            return;
        }

        var serverList = servers.Where(s => !string.IsNullOrEmpty(s.PoolName) || !string.IsNullOrEmpty(s.SiteName)).ToList();

        if (!serverList.Any())
        {
            _output.WriteLine("[INFO] No servers have IIS sites or pools configured");
            return;
        }

        try
        {
            _output.WriteLine($"[INFO] Stopping {serverList.Count} IIS servers...");

            var tasks = serverList.Select(async server =>
            {
                await StopServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI: true, cancellationToken);
            });

            await Task.WhenAll(tasks);
            _output.WriteLine("[OK] All IIS servers stopped.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to stop IIS: {ex.Message}");
        }
    }

    public async Task RestartAllIisAsync(IEnumerable<ServerInfo> servers, CancellationToken cancellationToken = default)
    {
        if (!_iisManager.IsSupported())
        {
            _output.WriteLine("[ERROR] IIS is not supported on this platform");
            return;
        }

        var serverList = servers.Where(s => !string.IsNullOrEmpty(s.PoolName) || !string.IsNullOrEmpty(s.SiteName)).ToList();

        if (!serverList.Any())
        {
            _output.WriteLine("[INFO] No servers have IIS sites or pools configured");
            return;
        }

        await StopAllIisAsync(serverList, cancellationToken).ConfigureAwait(false);
        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        await StartAllIisAsync(serverList, cancellationToken).ConfigureAwait(false);
    }

    private async Task PerformSyncthingOrchestrationAsync(
        IIisManager manager,
        ServerInfo localServerInfo,
        string nestedPath,
        ServerInfo[] remoteServers,
        ISyncthingMonitorService syncthingMonitor,
        CancellationToken cancellationToken)
    {
        _output.WriteLine("=== SYNCTHING ORCHESTRATION MODE ===");
        _output.WriteLine($"[INFO] Monitoring {remoteServers.Length} remote servers for Syncthing sync completion...");

        var serversWithoutConfig = remoteServers
            .Where(s => string.IsNullOrEmpty(s.SyncthingDeviceId) || s.SyncthingFolderIds.Count == 0)
            .ToList();

        if (serversWithoutConfig.Any())
        {
            _output.WriteLine($"[WARNING] {serversWithoutConfig.Count} servers missing Syncthing configuration:");
            foreach (var server in serversWithoutConfig)
            {
                var folderIdsStr = string.Join(", ", server.SyncthingFolderIds);
                _output.WriteLine($"    - {server.Name}: DeviceId={server.SyncthingDeviceId}, FolderIds=[{folderIdsStr}]");
            }
        }

        var serversToMonitor = remoteServers
            .Where(s => !string.IsNullOrEmpty(s.SyncthingDeviceId) && s.SyncthingFolderIds.Count > 0)
            .ToList();

        if (serversToMonitor.Count == 0)
        {
            _output.WriteLine("[ERROR] No servers with valid Syncthing configuration found!");
            return;
        }

        _output.WriteLine($"[INFO] Monitoring {serversToMonitor.Count} servers with valid Syncthing configuration");

        var completedServers = new ConcurrentBag<ServerInfo>();

        await _metricsService.MeasureAsync("syncthing_orchestration", async () =>
        {
            var completedList = await syncthingMonitor.WaitForMultipleServersAsync(
                serversToMonitor,
                async void (completedServer) =>
                {
                    _output.WriteLine($"[ORCHESTRATION] Server {completedServer.Name} sync completed! Starting services...");

                    if (!string.IsNullOrWhiteSpace(completedServer.PoolName))
                    {
                        var startPoolResult = await manager.StartAppPoolAsync(
                            completedServer.Name ?? "",
                            completedServer.PoolName,
                            cancellationToken);

                        if (startPoolResult.IsSuccess)
                        {
                            _output.WriteLine($"[OK] Started app pool '{completedServer.PoolName}' on {completedServer.Name}");
                        }
                        else
                        {
                            _output.WriteLine($"[ERROR] Failed to start app pool '{completedServer.PoolName}' on {completedServer.Name}: {startPoolResult.ErrorMessage}");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(completedServer.SiteName))
                    {
                        var startSiteResult = await manager.StartWebsiteAsync(
                            completedServer.Name ?? "",
                            completedServer.SiteName,
                            cancellationToken);

                        if (startSiteResult.IsSuccess)
                        {
                            _output.WriteLine($"[OK] Started website '{completedServer.SiteName}' on {completedServer.Name}");
                        }
                        else
                        {
                            _output.WriteLine($"[ERROR] Failed to start website '{completedServer.SiteName}' on {completedServer.Name}: {startSiteResult.ErrorMessage}");
                        }
                    }

                    completedServers.Add(completedServer);
                },
                cancellationToken);

            _output.WriteLine($"[INFO] Sync monitoring completed for {completedList.Count}/{serversToMonitor.Count} servers");

            _metricsService.SetGauge("syncthing_completed_servers", completedList.Count);
            _metricsService.SetGauge("syncthing_monitored_servers", serversToMonitor.Count);

            return Task.CompletedTask;
        }).ConfigureAwait(false);

        _output.WriteLine("[OK] All remote servers synchronized and started!");
        _output.WriteLine($"[INFO] Successfully started {completedServers.Count} remote servers");

        _output.WriteLine("[INFO] Starting local server...");
        await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, false, cancellationToken).ConfigureAwait(false);

        _output.WriteLine("=== SYNCTHING ORCHESTRATION COMPLETED ===");
        _metricsService.IncrementCounter("syncthing_orchestration_completed");
        _metricsService.IncrementCounter("successful_deployments_count");
    }
}
