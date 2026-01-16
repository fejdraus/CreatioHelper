using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Utils;
using CreatioHelper.ViewModels;

namespace CreatioHelper.Services;

public partial class OperationsService : ObservableObject, IOperationsService
{
    private readonly IOutputWriter _output;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly IIisManager _iisManager;
    private readonly ISiteSynchronizer _siteSynchronizer;
    private readonly IWorkspacePreparer _workspacePreparer;
    private readonly IRedisManagerFactory _redisManagerFactory;
    private readonly IMetricsService _metricsService;
    private readonly ISyncthingMonitorService? _syncthingMonitor;
    private readonly IServerStatusService _statusService;

    [ObservableProperty]
    private bool _isBusy;
    
    [ObservableProperty]
    private string _startButtonText = "Start";
    
    [ObservableProperty]
    private bool _isStopButtonEnabled;

    public OperationsService(
        IOutputWriter output,
        IIisManager iisManager,
        ISiteSynchronizer siteSynchronizer,
        IWorkspacePreparer workspacePreparer,
        IRedisManagerFactory redisManagerFactory,
        IMetricsService metricsService,
        IServerStatusService statusService,
        ISyncthingMonitorService? syncthingMonitor = null)
    {
        _output = output;
        _iisManager = iisManager;
        _siteSynchronizer = siteSynchronizer;
        _workspacePreparer = workspacePreparer;
        _redisManagerFactory = redisManagerFactory;
        _metricsService = metricsService;
        _statusService = statusService;
        _syncthingMonitor = syncthingMonitor;
    }

    private bool ExecutePreparerAction(Func<int> action, string errorMessage, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return false;

        int result = action();
        if (result != 0)
        {
            _output.WriteLine(errorMessage);
            return false;
        }
        return true;
    }

    public async Task StartOperation(MainWindowViewModel viewModel)
    {
        IsStopButtonEnabled = false;
        _output.Clear();

        if (!TryValidateInputs(viewModel, out var sitePath) || sitePath == null)
        {
            return;
        }

        // Create CancellationTokenSource BEFORE Task.Run
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        // Launch the operation on a background thread
        await Task.Run(async () =>
        {
            string packagesPath = viewModel.PackagesPath ?? "";
            string packagesBefore = viewModel.PackagesToDeleteBefore?.Trim() ?? "";
            string packagesAfter = viewModel.PackagesToDeleteAfter?.Trim() ?? "";
            var serverList = viewModel.ServerList.ToArray();
            var preparer = _workspacePreparer;
            IsBusy = true;
            StartButtonText = "In process...";
            viewModel.IsServerControlsEnabled = false;

            var quartzIsActiveOriginal = true;
            try
            {
                _output.WriteLine("Prepare WorkspaceConsole ...");

                preparer.Prepare(sitePath, out quartzIsActiveOriginal);

                if (cancellationToken.IsCancellationRequested)
                {
                    _metricsService.IncrementCounter("deployment_cancelled");
                    return;
                }

                if (viewModel.SelectedIisSite != null || !string.IsNullOrWhiteSpace(viewModel.SitePath))
                {
                    string nestedPath = Path.Combine(sitePath, "Terrasoft.WebApp", "bin", "Terrasoft.Common.dll");
                    var poolName = viewModel.IsIisMode ? viewModel.SelectedIisSite?.PoolName : null;
                    var siteName = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Name : null;
                    var appVersion = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Version : viewModel.SitePathWithVersion;
                    if (appVersion < new Version(7, 12, 0, 0))
                    {
                        _output.WriteLine("[ERROR] Creatio application not found.");
                        return;
                    }
                    var localServerInfo = new ServerInfo
                    {
                        Name = new ServerName(Environment.MachineName),
                        PoolName = poolName ?? string.Empty,
                        SiteName = siteName ?? string.Empty,
                        ServiceName = viewModel.ServiceName ?? string.Empty
                    };
                    var manager = _iisManager;

                    IsStopButtonEnabled = true;

                    // Track if schema rebuild was performed with server stop
                    bool schemaRebuildPerformed = false;
                    // Track if servers were already stopped for package operations
                    bool serversAlreadyStopped = false;

                    if (!string.IsNullOrWhiteSpace(packagesBefore) && appVersion >= Constants.MinimumVersionForDeletePackages)
                    {
                        // Stop local server before deleting packages (if not stopped later)
                        bool willStopLaterForPackages = !string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath);
                        if (!willStopLaterForPackages)
                        {
                            _output.WriteLine("Stopping local server before deleting packages...");
                            await PerformIisOperationsAsync(manager, localServerInfo, nestedPath, viewModel, viewModel.IsServerPanelVisible, cancellationToken).ConfigureAwait(false);
                        }

                        _output.WriteLine("Deleting packages BEFORE installation...");
                        
                        // Measure package deletion time BEFORE
                        bool success = false;
                        _metricsService.Measure("packages_delete_before", () =>
                        {
                            success = ExecutePreparerAction(() => preparer.DeletePackages(sitePath, packagesBefore), "[ERROR] Deleting packages failed.", cancellationToken);
                            if (!success) return;
                            
                            success = ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                            if (!success) return;
                            
                            success = ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                        });
                        
                        if (!success)
                        {
                            _output.WriteLine("[ERROR] Package deletion BEFORE installation failed. Stopping execution.");
                            _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = "packages_delete_before_failed" });
                            return;
                        }
                        
                        _metricsService.IncrementCounter("packages_deleted_before");
                    }

                    if (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath))
                    {
                        // Stop ALL servers (local + remote) before package installation
                        _output.WriteLine("Stopping ALL servers before package installation...");
                        await StopAllServersBeforeInstallation(manager, localServerInfo, nestedPath, serverList, viewModel.IsServerPanelVisible, cancellationToken).ConfigureAwait(false);
                        serversAlreadyStopped = true;  // Mark that servers were stopped for package install
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
                            return;
                        }
                        
                        _metricsService.IncrementCounter("packages_installed_count");
                    }

                    if (!string.IsNullOrWhiteSpace(packagesAfter) && appVersion >= Constants.MinimumVersionForDeletePackages)
                    {
                        // Stop local server before deleting packages (if not stopped earlier)
                        bool wasStoppedEarlier = !string.IsNullOrWhiteSpace(packagesBefore) ||
                                                (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath));
                        if (!wasStoppedEarlier)
                        {
                            _output.WriteLine("Stopping local server before deleting packages...");
                            await PerformIisOperationsAsync(manager, localServerInfo, nestedPath, viewModel, viewModel.IsServerPanelVisible, cancellationToken).ConfigureAwait(false);
                        }

                        _output.WriteLine("Deleting packages AFTER installation...");
                        
                        // Measure package deletion time AFTER
                        bool success = false;
                        _metricsService.Measure("packages_delete_after", () =>
                        {
                            success = ExecutePreparerAction(() => preparer.DeletePackages(sitePath, packagesAfter), "[ERROR] Deleting packages failed.", cancellationToken);
                            if (!success) return;
                            
                            success = ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                            if (!success) return;
                            
                            success = ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                        });
                        
                        if (!success)
                        {
                            _output.WriteLine("[ERROR] Package deletion AFTER installation failed. Stopping execution.");
                            _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = "packages_delete_after_failed" });
                            return;
                        }
                        
                        _metricsService.IncrementCounter("packages_deleted_after");
                    }

                    // If no package operations needed, perform schema regeneration and rebuild BEFORE synchronization
                    if (string.IsNullOrWhiteSpace(packagesPath) &&
                        string.IsNullOrWhiteSpace(packagesBefore) &&
                        string.IsNullOrWhiteSpace(packagesAfter))
                    {
                        schemaRebuildPerformed = true;

                        // If server panel is open with servers, stop all servers before rebuild
                        // If panel is closed, only local server will be stopped via PerformIisOperationsAsync
                        if (viewModel.IsServerPanelVisible && serverList.Length > 0)
                        {
                            _output.WriteLine("Stopping ALL servers (local + remote) before schema rebuild...");
                            await StopAllServersBeforeInstallation(manager, localServerInfo, nestedPath, serverList, viewModel.IsServerPanelVisible, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            _output.WriteLine("Stopping local server before schema rebuild...");
                            // Stop only local server when panel is closed
                            await PerformIisOperationsAsync(manager, localServerInfo, nestedPath, viewModel, viewModel.IsServerPanelVisible, cancellationToken).ConfigureAwait(false);
                        }

                        // Enable stop button during schema operations (user can cancel)
                        IsStopButtonEnabled = true;

                        // Measure schema generation operations time
                        _output.WriteLine("Performing schema regeneration and compilation...");
                        bool success = false;
                        _metricsService.Measure("schema_operations", () =>
                        {
                            success = ExecutePreparerAction(() => preparer.RegenerateSchemaSources(sitePath), "[ERROR] Failed to regenerate schema sources.", cancellationToken);
                            if (!success) return;

                            success = ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                            if (!success) return;

                            success = ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                        });

                        if (!success)
                        {
                            _output.WriteLine("[ERROR] Schema operations failed. Stopping execution.");
                            _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = "schema_operations_failed" });
                            return;
                        }

                        _output.WriteLine("[OK] Schema regeneration and compilation completed successfully.");
                    }

                    // Clear Redis cache immediately after all operations complete (before synchronization and IIS start)
                    // This ensures IIS pools will have clean cache when they start
                    var redisManager = _redisManagerFactory.Create(sitePath);
                    var redisStatus = redisManager.CheckStatus();
                    if (redisStatus)
                    {
                        redisManager.Clear();
                        _output.WriteLine("[OK] Redis cache cleared.");
                    }

                    bool usedSyncthingOrchestration = false;
                    bool usedManagePoolsOnly = false;

                    // Perform synchronization only if schema rebuild was NOT performed
                    // (schema rebuild is a local-only operation, sync happens after)
                    if (!schemaRebuildPerformed && OperatingSystem.IsWindows() && serverList.Length > 0 && viewModel.IsServerPanelVisible)
                    {
                        IsStopButtonEnabled = false;
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            // Choose synchronization mode based on settings
                            if (viewModel.UseSyncthingForSync && _syncthingMonitor != null)
                            {
                                // Syncthing orchestration mode (servers are started automatically after sync completion)
                                await PerformSyncthingOrchestrationAsync(manager, localServerInfo, nestedPath, serverList, cancellationToken).ConfigureAwait(false);
                                usedSyncthingOrchestration = true;
                            }
                            else if (viewModel.EnableFileCopySynchronization)
                            {
                                // Full synchronization with file copying
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
                                    return;
                                }
                            }
                            else
                            {
                                // Pool management only - for external file sync (built-in sync system, etc.)
                                // Pass serversAlreadyStopped flag to skip stop if servers were stopped for package install
                                await ManageServerPoolsOnlyAsync(new List<ServerInfo>(serverList), viewModel.IsServerPanelVisible, serversAlreadyStopped, cancellationToken).ConfigureAwait(false);
                                usedManagePoolsOnly = true;
                            }
                        }
                        IsStopButtonEnabled = true;
                    }

                    IsStopButtonEnabled = false;

                    // Start IIS pools/sites only if:
                    // - Syncthing orchestration was NOT used (it starts servers automatically)
                    // - ManagePoolsOnly was NOT used (it already started servers after external sync)
                    if (!usedSyncthingOrchestration && !usedManagePoolsOnly)
                    {
                        bool hasRemoteServers = viewModel.IsServerPanelVisible && serverList.Length > 0;

                        // Measure startup operation time
                        await _metricsService.MeasureAsync("startup_operations", async () =>
                        {
                            if ((schemaRebuildPerformed || serversAlreadyStopped) && hasRemoteServers)
                            {
                                // Schema rebuild or package install was performed with remote servers - start all servers
                                await StartAllServersAfterRebuild(manager, localServerInfo, nestedPath, serverList, viewModel.IsServerPanelVisible, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                // Normal flow - just start local server
                                await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, viewModel.IsServerPanelVisible, cancellationToken).ConfigureAwait(false);
                            }
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _output.WriteLine("[INFO] Operation was cancelled.");
                _metricsService.IncrementCounter("deployment_cancelled");
                IsStopButtonEnabled = true;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] {ex.Message}");
                _metricsService.IncrementCounter("failed_deployments_count", new() { ["error_type"] = ex.GetType().Name });
                IsStopButtonEnabled = true;
            }
            finally
            {
                IsBusy = false;
                StartButtonText = "Start";
                viewModel.IsServerControlsEnabled = true;
                IsStopButtonEnabled = true;
                if (!quartzIsActiveOriginal)
                {
                    string config = Directory.Exists(Path.Combine(sitePath, "Terrasoft.WebApp"))
                        ? Path.Combine(sitePath, "Web.config")
                        : Path.Combine(sitePath, "Terrasoft.WebHost.dll.config");
                    preparer.UpdateOutConfig(config, quartzIsActiveOriginal);
                }
            }
        }, cancellationToken);
    }

    #region Server Management Core Methods

    /// <summary>
    /// Refreshes server status on UI thread if panel is visible
    /// Universal method for all synchronization modes
    /// </summary>
    private async Task RefreshServerStatusIfNeededAsync(ServerInfo server, bool isServerPanelVisible, CancellationToken cancellationToken)
    {
        if (isServerPanelVisible && OperatingSystem.IsWindows())
        {
            await Task.Delay(500, cancellationToken); // Give IIS a moment to update
            await _statusService.RefreshServerStatusOnUIThreadAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refreshes multiple server statuses on UI thread if panel is visible
    /// Universal method for all synchronization modes
    /// </summary>
    private async Task RefreshMultipleServerStatusIfNeededAsync(ServerInfo[] servers, bool isServerPanelVisible, CancellationToken cancellationToken)
    {
        if (isServerPanelVisible && OperatingSystem.IsWindows() && servers.Length > 0)
        {
            await Task.Delay(500, cancellationToken); // Give IIS a moment to update
            await _statusService.RefreshMultipleServerStatusOnUIThreadAsync(servers, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops a single server (pool, site, or service)
    /// </summary>
    /// <returns>Tuple (poolStopped, siteStopped, serviceStopped)</returns>
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

        // For remote servers - always IIS mode (they don't have local nestedPath)
        // For local server - check if file exists to determine IIS vs Service mode
        bool isIisMode = !isLocal || (nestedPath != null && File.Exists(nestedPath));

        if (OperatingSystem.IsWindows() && isIisMode)
        {
            // Stop IIS pool
            if (!string.IsNullOrWhiteSpace(server.PoolName))
            {
                var result = await _iisManager.StopAppPoolAsync(serverName, server.PoolName, cancellationToken);
                poolStopped = result.IsSuccess;
                if (poolStopped)
                {
                    if (isLocal)
                    {
                        _output.WriteLine($"[INFO] {serverLabel} Pool stopped.");
                    }
                    else
                    {
                        _output.WriteLine($"[INFO] Stopped app pool '{server.PoolName}' on {server.Name}");
                    }
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to stop app pool '{server.PoolName}' on {serverLabel}: {result.ErrorMessage}");
                }
            }

            // Stop IIS site
            if (!string.IsNullOrWhiteSpace(server.SiteName))
            {
                var result = await _iisManager.StopWebsiteAsync(serverName, server.SiteName, cancellationToken);
                siteStopped = result.IsSuccess;
                if (siteStopped)
                {
                    if (isLocal)
                    {
                        _output.WriteLine($"[INFO] {serverLabel} Website stopped.");
                    }
                    else
                    {
                        _output.WriteLine($"[INFO] Stopped website '{server.SiteName}' on {server.Name}");
                    }
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to stop website '{server.SiteName}' on {serverLabel}: {result.ErrorMessage}");
                }
            }
        }

        // Stop Windows Service (if not IIS mode)
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

        // Refresh UI immediately after stopping (universal for all sync modes)
        await RefreshServerStatusIfNeededAsync(server, shouldRefreshUI, cancellationToken);

        return (poolStopped, siteStopped, serviceStopped);
    }

    /// <summary>
    /// Starts a single server (pool, site, or service)
    /// </summary>
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

        // For remote servers - always IIS mode (they don't have local nestedPath)
        // For local server - check if file exists to determine IIS vs Service mode
        bool isIisMode = !isLocal || (nestedPath != null && File.Exists(nestedPath));

        if (OperatingSystem.IsWindows() && isIisMode)
        {
            // Start IIS pool if it was stopped
            if (poolWasStopped && !string.IsNullOrWhiteSpace(server.PoolName))
            {
                var result = await _iisManager.StartAppPoolAsync(serverName, server.PoolName, cancellationToken);
                if (result.IsSuccess)
                {
                    if (isLocal)
                    {
                        _output.WriteLine($"[INFO] {serverLabel} Pool is running.");
                    }
                    else
                    {
                        _output.WriteLine($"[INFO] Started app pool '{server.PoolName}' on {server.Name}");
                    }
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to start app pool '{server.PoolName}' on {serverLabel}: {result.ErrorMessage}");
                }
            }

            // Start IIS site if it was stopped
            if (siteWasStopped && !string.IsNullOrWhiteSpace(server.SiteName))
            {
                var result = await _iisManager.StartWebsiteAsync(serverName, server.SiteName, cancellationToken);
                if (result.IsSuccess)
                {
                    if (isLocal)
                    {
                        _output.WriteLine($"[INFO] {serverLabel} Website is running.");
                    }
                    else
                    {
                        _output.WriteLine($"[INFO] Started website '{server.SiteName}' on {server.Name}");
                    }
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to start website '{server.SiteName}' on {serverLabel}: {result.ErrorMessage}");
                }
            }
        }

        // Start Windows Service if it was stopped
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

        // Refresh UI immediately after starting (universal for all sync modes)
        await RefreshServerStatusIfNeededAsync(server, shouldRefreshUI, cancellationToken);
    }

    #endregion

    private async Task PerformIisOperationsAsync(IIisManager manager, ServerInfo localServerInfo, string nestedPath, MainWindowViewModel viewModel, bool shouldRefreshUI, CancellationToken cancellationToken)
    {
        // Ensure ServiceName is set for non-IIS mode
        if (!File.Exists(nestedPath) && !string.IsNullOrWhiteSpace(viewModel.ServiceName))
        {
            localServerInfo.ServiceName = viewModel.ServiceName;
        }

        await StopServerAsync(localServerInfo, isLocal: true, nestedPath, shouldRefreshUI, cancellationToken);
    }

    private async Task PerformStartupOperationsAsync(IIisManager manager, ServerInfo localServerInfo, string nestedPath, bool shouldRefreshUI, CancellationToken cancellationToken)
    {
        // Assume everything was stopped before (pool, site, service all = true)
        await StartServerAsync(localServerInfo, isLocal: true, nestedPath, shouldRefreshUI,
            poolWasStopped: true, siteWasStopped: true, serviceWasStopped: true, cancellationToken);
    }

    public void StopOperation() 
    {
        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested) 
        {
            _cancellationTokenSource.Cancel();
            _output.WriteLine("[INFO] Cancelling operations...");
        }
        try
        {
            int killed = 0;

            foreach (var process in Process.GetProcessesByName("Terrasoft.Tools.WorkspaceConsole"))
            {
                try
                {
                    process.Kill();
                    killed++;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[ERROR] Failed to kill process {process.Id}: {ex.Message}");
                }
            }

            foreach (var process in Process.GetProcessesByName("dotnet"))
            {
                try
                {
                    string cmd = GetCommandLine(process);
                    if (cmd.Contains("Terrasoft.Tools.WorkspaceConsole.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        killed++;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[ERROR] Failed to inspect process {process.Id}: {ex.Message}");
                }
            }

            _output.WriteLine(killed > 0
                ? $"[INFO] Terminated {killed} WorkspaceConsole processes."
                : "[INFO] No WorkspaceConsole processes found to terminate.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to terminate WorkspaceConsole processes: {ex.Message}");
        }
        IsBusy = false;
        StartButtonText = "Start";
    }

    private bool TryValidateInputs(MainWindowViewModel viewModel, out string? sitePath)
    {
        sitePath = viewModel.IsIisMode
            ? viewModel.SelectedIisSite?.Path
            : viewModel.SitePath;

        if (string.IsNullOrWhiteSpace(sitePath)) 
        {
            _output.WriteLine("The path to the site is not indicated.");
            return false;
        }

        return true;
    }

    private static string GetCommandLine(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
                using var results = searcher.Get();
                foreach (var o in results)
                {
                    var obj = (ManagementObject)o;
                    return obj["CommandLine"]?.ToString() ?? string.Empty;
                }
            }
            else
            {
                string path = $"/proc/{process.Id}/cmdline";
                if (File.Exists(path))
                {
                    return File.ReadAllText(path).Replace('\0', ' ');
                }
            }
        }
        catch
        {
            // Ignore retrieval errors
        }

        return string.Empty;
    }

    /// <summary>
    /// Manages server pools without file synchronization - for external sync tools like Syncthing
    /// </summary>
    private async Task ManageServerPoolsOnlyAsync(List<ServerInfo> targetServers, bool shouldRefreshUI, bool serversAlreadyStopped, CancellationToken cancellationToken)
    {
        _output.WriteLine("[INFO] Managing server pools without file synchronization...");

        // Stop all server pools and sites (skip if already stopped for package operations)
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

            // Verify services are stopped
            _output.WriteLine("[INFO] Verifying services are stopped...");
            await Task.Delay(3000, cancellationToken);
        }
        else
        {
            // Servers were already stopped for package operations, mark all as stopped
            foreach (var server in targetServers)
            {
                stopResults[server] = (true, true, false); // pool, site stopped; service not used for remote
            }
            _output.WriteLine("[INFO] Servers already stopped from package operations.");
        }

        // Start services back
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

    /// <summary>
    /// Stops all servers (local + remote) before package installation
    /// </summary>
    private async Task StopAllServersBeforeInstallation(IIisManager manager, ServerInfo localServerInfo, string nestedPath, ServerInfo[] remoteServers, bool shouldRefreshUI, CancellationToken cancellationToken)
    {
        // First stop local server
        await StopServerAsync(localServerInfo, isLocal: true, nestedPath, shouldRefreshUI, cancellationToken);

        // Then stop all remote servers
        foreach (var server in remoteServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _output.WriteLine("[INFO] Stopping servers was cancelled.");
                return;
            }

            await Task.Delay(1000, cancellationToken); // Small delay between operations
            await StopServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI, cancellationToken);
        }

        // Wait for all services to fully stop
        _output.WriteLine("[INFO] Waiting for ALL services to fully stop...");
        await Task.Delay(3000, cancellationToken);

        _output.WriteLine("[OK] All servers (local + remote) stopped successfully.");
    }

    /// <summary>
    /// Starts all servers (local + remote) after schema rebuild completes
    /// </summary>
    private async Task StartAllServersAfterRebuild(IIisManager manager, ServerInfo localServerInfo, string nestedPath, ServerInfo[] remoteServers, bool shouldRefreshUI, CancellationToken cancellationToken)
    {
        _output.WriteLine("Starting ALL servers (local + remote) after schema rebuild...");

        // First start local server
        await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, shouldRefreshUI, cancellationToken).ConfigureAwait(false);

        // Then start all remote servers
        foreach (var server in remoteServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _output.WriteLine("[INFO] Starting servers was cancelled.");
                return;
            }

            await Task.Delay(1000, cancellationToken); // Small delay between operations
            // Assume all were stopped before (pool, site = true; service = false for remote)
            await StartServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI,
                poolWasStopped: true, siteWasStopped: true, serviceWasStopped: false, cancellationToken);
        }

        _output.WriteLine("[OK] All servers (local + remote) started successfully.");
    }

    /// <summary>
    /// Performs Syncthing orchestration workflow:
    /// 1. Operations complete (packages deleted/installed/compiled)
    /// 2. Syncthing automatically syncs files in background
    /// 3. Monitor each remote server's sync completion in parallel
    /// 4. Start each remote server immediately when its sync completes
    /// 5. Start local server after all remote servers are ready
    /// </summary>
    private async Task PerformSyncthingOrchestrationAsync(
        IIisManager manager,
        ServerInfo localServerInfo,
        string nestedPath,
        ServerInfo[] remoteServers,
        CancellationToken cancellationToken)
    {
        _output.WriteLine("=== SYNCTHING ORCHESTRATION MODE ===");
        _output.WriteLine($"[INFO] Monitoring {remoteServers.Length} remote servers for Syncthing sync completion...");

        if (_syncthingMonitor == null)
        {
            _output.WriteLine("[ERROR] Syncthing monitor service is not configured!");
            return;
        }

        // Validate that all servers have Syncthing configuration
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

        // Start monitoring all remote servers in parallel
        var completedServers = new System.Collections.Concurrent.ConcurrentBag<ServerInfo>();

        await _metricsService.MeasureAsync("syncthing_orchestration", async () =>
        {
            var completedList = await _syncthingMonitor.WaitForMultipleServersAsync(
                serversToMonitor,
                async void (completedServer) =>
                {
                    // Callback: Start this server immediately when sync completes
                    _output.WriteLine($"[ORCHESTRATION] Server {completedServer.Name} sync completed! Starting services...");

                    // Start pool if configured
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

                    // Start site if configured
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

        // All remote servers finished syncing and started
        _output.WriteLine("[OK] All remote servers synchronized and started!");
        _output.WriteLine($"[INFO] Successfully started {completedServers.Count} remote servers");

        // Now start local server (Syncthing callbacks will update status)
        _output.WriteLine("[INFO] Starting local server...");
        await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, false, cancellationToken).ConfigureAwait(false);

        _output.WriteLine("=== SYNCTHING ORCHESTRATION COMPLETED ===");
        _metricsService.IncrementCounter("syncthing_orchestration_completed");
        _metricsService.IncrementCounter("successful_deployments_count");
    }

    /// <summary>
    /// Get the current Syncthing monitor service instance
    /// </summary>
    public ISyncthingMonitorService? GetSyncthingMonitor()
    {
        return _syncthingMonitor;
    }

    /// <summary>
    /// Start all IIS sites and application pools for servers in the list
    /// </summary>
    public async Task StartAllIisAsync(IEnumerable<ServerInfo> servers)
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

            // Start all servers in parallel with UI updates
            var tasks = serverList.Select(async server =>
            {
                // Assume all were stopped (pool, site, service)
                await StartServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI: true,
                    poolWasStopped: true, siteWasStopped: true, serviceWasStopped: false, CancellationToken.None);
            });

            await Task.WhenAll(tasks);
            _output.WriteLine("[OK] All IIS servers started.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to start IIS: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop all IIS sites and application pools for servers in the list
    /// </summary>
    public async Task StopAllIisAsync(IEnumerable<ServerInfo> servers)
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

            // Stop all servers in parallel with UI updates
            var tasks = serverList.Select(async server =>
            {
                await StopServerAsync(server, isLocal: false, nestedPath: null, shouldRefreshUI: true, CancellationToken.None);
            });

            await Task.WhenAll(tasks);
            _output.WriteLine("[OK] All IIS servers stopped.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to stop IIS: {ex.Message}");
        }
    }
}
