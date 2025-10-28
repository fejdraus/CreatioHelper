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
        ISyncthingMonitorService? syncthingMonitor = null)
    {
        _output = output;
        _iisManager = iisManager;
        _siteSynchronizer = siteSynchronizer;
        _workspacePreparer = workspacePreparer;
        _redisManagerFactory = redisManagerFactory;
        _metricsService = metricsService;
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

                    // Measure IIS operation time
                    await _metricsService.MeasureAsync("iis_operations", async () =>
                    {
                        await PerformIisOperationsAsync(manager, localServerInfo, nestedPath, viewModel, cancellationToken).ConfigureAwait(false);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);

                    IsStopButtonEnabled = true;

                    if (!string.IsNullOrWhiteSpace(packagesBefore) && appVersion >= Constants.MinimumVersionForDeletePackages)
                    {
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
                        await StopAllServersBeforeInstallation(manager, localServerInfo, nestedPath, serverList, cancellationToken).ConfigureAwait(false);
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
                        // If server panel is open with servers, stop all servers before rebuild
                        // If panel is closed, only local server will be stopped via PerformIisOperationsAsync
                        if (viewModel.IsServerPanelVisible && serverList.Length > 0)
                        {
                            _output.WriteLine("Stopping ALL servers (local + remote) before schema rebuild...");
                            await StopAllServersBeforeInstallation(manager, localServerInfo, nestedPath, serverList, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            _output.WriteLine("Stopping local server before schema rebuild...");
                            // Stop only local server when panel is closed
                            await PerformIisOperationsAsync(manager, localServerInfo, nestedPath, viewModel, cancellationToken).ConfigureAwait(false);
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

                    if (OperatingSystem.IsWindows() && serverList.Length > 0 && viewModel.IsServerPanelVisible)
                    {
                        IsStopButtonEnabled = false;
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            // Choose synchronization mode based on settings
                            if (viewModel.UseSyncthingForSync && _syncthingMonitor != null)
                            {
                                // Syncthing orchestration mode
                                await PerformSyncthingOrchestrationAsync(manager, localServerInfo, nestedPath, serverList, cancellationToken).ConfigureAwait(false);
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
                                await ManageServerPoolsOnlyAsync(new List<ServerInfo>(serverList), cancellationToken).ConfigureAwait(false);
                            }
                        }
                        IsStopButtonEnabled = true;
                    }

                    var redisManager = _redisManagerFactory.Create(sitePath);
                    var redisStatus = redisManager.CheckStatus();
                    if (redisStatus)
                    {
                        redisManager.Clear();
                    }

                    IsStopButtonEnabled = false;

                    // Determine whether to start all servers (rebuild scenario with remote servers) or just local server
                    bool rebuildPerformed = string.IsNullOrWhiteSpace(packagesPath) &&
                                           string.IsNullOrWhiteSpace(packagesBefore) &&
                                           string.IsNullOrWhiteSpace(packagesAfter);
                    bool hasRemoteServers = viewModel.IsServerPanelVisible && serverList.Length > 0;

                    // Measure startup operation time
                    await _metricsService.MeasureAsync("startup_operations", async () =>
                    {
                        if (rebuildPerformed && hasRemoteServers)
                        {
                            // Schema rebuild was performed with remote servers - start all servers
                            await StartAllServersAfterRebuild(manager, localServerInfo, nestedPath, serverList, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // Normal flow - just start local server
                            await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, cancellationToken).ConfigureAwait(false);
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
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

    private async Task PerformIisOperationsAsync(IIisManager manager, ServerInfo localServerInfo, string nestedPath, MainWindowViewModel viewModel, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(nestedPath))
            {
                if (!string.IsNullOrWhiteSpace(localServerInfo.PoolName))
                {
                    var stopPoolResult = await manager.StopAppPoolAsync(Environment.MachineName, localServerInfo.PoolName, cancellationToken).ConfigureAwait(false);
                    if (stopPoolResult.IsSuccess)
                    {
                        _output.WriteLine("[INFO] Main Pool stopped.");
                    }
                    else
                    {
                        _output.WriteLine($"[ERROR] Failed to stop application pool: {stopPoolResult.ErrorMessage}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(localServerInfo.SiteName))
                {
                    var stopSiteResult = await manager.StopWebsiteAsync(Environment.MachineName, localServerInfo.SiteName, cancellationToken).ConfigureAwait(false);
                    if (stopSiteResult.IsSuccess)
                    {
                        _output.WriteLine("[INFO] Main Website stopped.");
                    }
                    else
                    {
                        _output.WriteLine($"[ERROR] Failed to stop website: {stopSiteResult.ErrorMessage}");
                    }
                }
            }
        }
        if (!File.Exists(nestedPath) && !string.IsNullOrWhiteSpace(viewModel.ServiceName))
        {
            localServerInfo.ServiceName = viewModel.ServiceName;
            var serviceStopResult = await manager.StopServiceAsync(Environment.MachineName, localServerInfo.ServiceName, cancellationToken).ConfigureAwait(false);
            if (serviceStopResult.IsSuccess)
            {
                _output.WriteLine("[INFO] Main Service stopped.");
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to stop service: {serviceStopResult.ErrorMessage}");
            }
        }
    }

    private async Task PerformStartupOperationsAsync(IIisManager manager, ServerInfo localServerInfo, string nestedPath, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(nestedPath))
            {
                if (!string.IsNullOrWhiteSpace(localServerInfo.PoolName)) 
                {
                    var startPoolResult = await manager.StartAppPoolAsync(Environment.MachineName, localServerInfo.PoolName, cancellationToken).ConfigureAwait(false);
                    if (startPoolResult.IsSuccess)
                    {
                        _output.WriteLine("[INFO] Main Pool is running.");
                    }
                    else
                    {
                        _output.WriteLine($"[ERROR] Failed to start application pool: {startPoolResult.ErrorMessage}");
                    }
                }
                if (!string.IsNullOrWhiteSpace(localServerInfo.SiteName))
                {
                    var startSiteResult = await manager.StartWebsiteAsync(Environment.MachineName, localServerInfo.SiteName, cancellationToken).ConfigureAwait(false);
                    if (startSiteResult.IsSuccess)
                    {
                        _output.WriteLine("[INFO] Main Website is running.");
                    }
                    else
                    {
                        _output.WriteLine($"[ERROR] Failed to start website: {startSiteResult.ErrorMessage}");
                    }
                }
            }

            if (!File.Exists(nestedPath) && !string.IsNullOrWhiteSpace(localServerInfo.ServiceName))
            {
                var serviceStartResult = await manager.StartServiceAsync(Environment.MachineName, localServerInfo.ServiceName, cancellationToken).ConfigureAwait(false);
                if (serviceStartResult.IsSuccess)
                {
                    _output.WriteLine("[INFO] Main Service is running.");
                }
                else
                {
                    _output.WriteLine("[WARNING] Failed to start main service.");
                }
            }
        }
        else
        {
            if (!File.Exists(nestedPath) && !string.IsNullOrWhiteSpace(localServerInfo.ServiceName))
            {
                var serviceStartResult = await manager.StartServiceAsync(Environment.MachineName, localServerInfo.ServiceName, cancellationToken).ConfigureAwait(false);
                if (serviceStartResult.IsSuccess)
                {
                    _output.WriteLine("[INFO] Main Service is running.");
                }
                else
                {
                    _output.WriteLine("[WARNING] Failed to start main service.");
                }
            }
        }
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
    private async Task ManageServerPoolsOnlyAsync(List<ServerInfo> targetServers, CancellationToken cancellationToken)
    {
        _output.WriteLine("[INFO] Managing server pools without file synchronization...");

        // Stop all server pools and sites
        var stopResults = new Dictionary<ServerInfo, (bool poolStopped, bool siteStopped)>();
        foreach (var server in targetServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _output.WriteLine("[INFO] Pool management was cancelled.");
                return;
            }

            bool poolStopped = true;
            bool siteStopped = true;

            if (!string.IsNullOrWhiteSpace(server.PoolName))
            {
                var stopPoolResult = await _iisManager.StopAppPoolAsync(server.Name ?? "", server.PoolName, cancellationToken);
                poolStopped = stopPoolResult.IsSuccess;
                if (!poolStopped)
                {
                    _output.WriteLine($"[WARN] Failed to stop app pool '{server.PoolName}' on {server.Name}: {stopPoolResult.ErrorMessage}");
                }
            }

            if (!string.IsNullOrWhiteSpace(server.SiteName))
            {
                var stopSiteResult = await _iisManager.StopWebsiteAsync(server.Name ?? "", server.SiteName, cancellationToken);
                siteStopped = stopSiteResult.IsSuccess;
                if (!siteStopped)
                {
                    _output.WriteLine($"[WARN] Failed to stop website '{server.SiteName}' on {server.Name}: {stopSiteResult.ErrorMessage}");
                }
            }

            stopResults[server] = (poolStopped, siteStopped);
        }

        // Verify services are stopped
        _output.WriteLine("[INFO] Verifying services are stopped...");
        await Task.Delay(3000, cancellationToken);

        await Task.Run(async () =>
        {
            // Start services back
            foreach (var server in targetServers)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _output.WriteLine("[INFO] Pool management restart was cancelled.");
                    return;
                }

                var (poolWasStopped, siteWasStopped) = stopResults[server];

                // Start pool if it was stopped
                if (poolWasStopped && !string.IsNullOrWhiteSpace(server.PoolName))
                {
                    var startPoolResult = await _iisManager.StartAppPoolAsync(server.Name ?? "", server.PoolName, cancellationToken);
                    if (startPoolResult.IsSuccess)
                    {
                        _output.WriteLine($"[INFO] Started app pool '{server.PoolName}' on {server.Name}");
                    }
                    else
                    {
                        _output.WriteLine($"[WARN] Failed to start app pool '{server.PoolName}' on {server.Name}: {startPoolResult.ErrorMessage}");
                    }
                }

                // Start site if it was stopped
                if (siteWasStopped && !string.IsNullOrWhiteSpace(server.SiteName))
                {
                    var startSiteResult = await _iisManager.StartWebsiteAsync(server.Name ?? "", server.SiteName, cancellationToken);
                    if (startSiteResult.IsSuccess)
                    {
                        _output.WriteLine($"[INFO] Started website '{server.SiteName}' on {server.Name}");
                    }
                    else
                    {
                        _output.WriteLine($"[WARN] Failed to start website '{server.SiteName}' on {server.Name}: {startSiteResult.ErrorMessage}");
                    }
                }
            }
        }, cancellationToken);

        _output.WriteLine("[OK] Server pool management completed (external sync mode).");
        _metricsService.IncrementCounter("pool_management_completed");
    }

    /// <summary>
    /// Stops all servers (local + remote) before package installation
    /// </summary>
    private async Task StopAllServersBeforeInstallation(IIisManager manager, ServerInfo localServerInfo, string nestedPath, ServerInfo[] remoteServers, CancellationToken cancellationToken)
    {
        // First stop local server
        if (OperatingSystem.IsWindows() && File.Exists(nestedPath))
        {
            if (!string.IsNullOrWhiteSpace(localServerInfo.PoolName))
            {
                var stopPoolResult = await manager.StopAppPoolAsync(Environment.MachineName, localServerInfo.PoolName, cancellationToken).ConfigureAwait(false);
                if (stopPoolResult.IsSuccess)
                {
                    _output.WriteLine("[INFO] Local Pool stopped.");
                }
                else
                {
                    _output.WriteLine($"[ERROR] Failed to stop local application pool: {stopPoolResult.ErrorMessage}");
                }
            }

            if (!string.IsNullOrWhiteSpace(localServerInfo.SiteName))
            {
                var stopSiteResult = await manager.StopWebsiteAsync(Environment.MachineName, localServerInfo.SiteName, cancellationToken).ConfigureAwait(false);
                if (stopSiteResult.IsSuccess)
                {
                    _output.WriteLine("[INFO] Local Website stopped.");
                }
                else
                {
                    _output.WriteLine($"[ERROR] Failed to stop local website: {stopSiteResult.ErrorMessage}");
                }
            }
        }

        // Then stop all remote servers
        foreach (var server in remoteServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _output.WriteLine("[INFO] Stopping servers was cancelled.");
                return;
            }

            await Task.Delay(1000, cancellationToken); // Small delay between operations

            if (!string.IsNullOrWhiteSpace(server.PoolName))
            {
                var stopPoolResult = await _iisManager.StopAppPoolAsync(server.Name ?? "", server.PoolName, cancellationToken);
                if (stopPoolResult.IsSuccess)
                {
                    _output.WriteLine($"[INFO] Stopped app pool '{server.PoolName}' on {server.Name}");
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to stop app pool '{server.PoolName}' on {server.Name}: {stopPoolResult.ErrorMessage}");
                }
            }

            if (!string.IsNullOrWhiteSpace(server.SiteName))
            {
                var stopSiteResult = await _iisManager.StopWebsiteAsync(server.Name ?? "", server.SiteName, cancellationToken);
                if (stopSiteResult.IsSuccess)
                {
                    _output.WriteLine($"[INFO] Stopped website '{server.SiteName}' on {server.Name}");
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to stop website '{server.SiteName}' on {server.Name}: {stopSiteResult.ErrorMessage}");
                }
            }
        }

        // Wait for all services to fully stop
        _output.WriteLine("[INFO] Waiting for ALL services to fully stop...");
        await Task.Delay(3000, cancellationToken);

        _output.WriteLine("[OK] All servers (local + remote) stopped successfully.");
    }

    /// <summary>
    /// Starts all servers (local + remote) after schema rebuild completes
    /// </summary>
    private async Task StartAllServersAfterRebuild(IIisManager manager, ServerInfo localServerInfo, string nestedPath, ServerInfo[] remoteServers, CancellationToken cancellationToken)
    {
        _output.WriteLine("Starting ALL servers (local + remote) after schema rebuild...");

        // First start local server
        await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, cancellationToken).ConfigureAwait(false);

        // Then start all remote servers
        foreach (var server in remoteServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _output.WriteLine("[INFO] Starting servers was cancelled.");
                return;
            }

            await Task.Delay(1000, cancellationToken); // Small delay between operations

            if (!string.IsNullOrWhiteSpace(server.PoolName))
            {
                var startPoolResult = await _iisManager.StartAppPoolAsync(server.Name ?? "", server.PoolName, cancellationToken);
                if (startPoolResult.IsSuccess)
                {
                    _output.WriteLine($"[INFO] Started app pool '{server.PoolName}' on {server.Name}");
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to start app pool '{server.PoolName}' on {server.Name}: {startPoolResult.ErrorMessage}");
                }
            }

            if (!string.IsNullOrWhiteSpace(server.SiteName))
            {
                var startSiteResult = await _iisManager.StartWebsiteAsync(server.Name ?? "", server.SiteName, cancellationToken);
                if (startSiteResult.IsSuccess)
                {
                    _output.WriteLine($"[INFO] Started website '{server.SiteName}' on {server.Name}");
                }
                else
                {
                    _output.WriteLine($"[WARN] Failed to start website '{server.SiteName}' on {server.Name}: {startSiteResult.ErrorMessage}");
                }
            }
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

        // Now start local server
        _output.WriteLine("[INFO] Starting local server...");
        await PerformStartupOperationsAsync(manager, localServerInfo, nestedPath, cancellationToken).ConfigureAwait(false);

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
            foreach (var server in serverList)
            {
                // Start application pool if configured
                if (!string.IsNullOrEmpty(server.PoolName))
                {
                    var result = await _iisManager.StartAppPoolAsync(server.Name ?? "localhost", server.PoolName, CancellationToken.None);
                    if (!result.IsSuccess)
                    {
                        _output.WriteLine($"[WARNING] Failed to start app pool {server.PoolName}: {result.ErrorMessage}");
                    }
                }

                // Start website if configured
                if (!string.IsNullOrEmpty(server.SiteName))
                {
                    var result = await _iisManager.StartWebsiteAsync(server.Name ?? "localhost", server.SiteName, CancellationToken.None);
                    if (!result.IsSuccess)
                    {
                        _output.WriteLine($"[WARNING] Failed to start site {server.SiteName}: {result.ErrorMessage}");
                    }
                }
            }
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
            foreach (var server in serverList)
            {
                // Stop website first if configured
                if (!string.IsNullOrEmpty(server.SiteName))
                {
                    var result = await _iisManager.StopWebsiteAsync(server.Name ?? "localhost", server.SiteName, CancellationToken.None);
                    if (!result.IsSuccess)
                    {
                        _output.WriteLine($"[WARNING] Failed to stop site {server.SiteName}: {result.ErrorMessage}");
                    }
                }

                // Stop application pool if configured
                if (!string.IsNullOrEmpty(server.PoolName))
                {
                    var result = await _iisManager.StopAppPoolAsync(server.Name ?? "localhost", server.PoolName, CancellationToken.None);
                    if (!result.IsSuccess)
                    {
                        _output.WriteLine($"[WARNING] Failed to stop app pool {server.PoolName}: {result.ErrorMessage}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to stop IIS: {ex.Message}");
        }
    }
}
