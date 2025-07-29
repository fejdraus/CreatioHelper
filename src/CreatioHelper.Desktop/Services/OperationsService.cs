using System;
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
    private readonly IRemoteIisManager _remoteIisManager;
    private readonly ISiteSynchronizer _siteSynchronizer;
    private readonly IWorkspacePreparer _workspacePreparer;
    private readonly IRedisManagerFactory _redisManagerFactory;
    private readonly IMetricsService _metricsService;

    [ObservableProperty]
    private bool _isBusy;
    
    [ObservableProperty]
    private string _startButtonText = "Start";
    
    [ObservableProperty]
    private bool _isStopButtonEnabled;

    public OperationsService(
        IOutputWriter output,
        IRemoteIisManager remoteIisManager,
        ISiteSynchronizer siteSynchronizer,
        IWorkspacePreparer workspacePreparer,
        IRedisManagerFactory redisManagerFactory,
        IMetricsService metricsService)
    {
        _output = output;
        _remoteIisManager = remoteIisManager;
        _siteSynchronizer = siteSynchronizer;
        _workspacePreparer = workspacePreparer;
        _redisManagerFactory = redisManagerFactory;
        _metricsService = metricsService;
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
        await Task.Run(() =>
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
                    var manager = _remoteIisManager;

                    // Measure IIS operation time
                    _metricsService.Measure("iis_operations", () =>
                    {
                        PerformIisOperations(manager, localServerInfo, nestedPath, viewModel, cancellationToken);
                    });

                    IsStopButtonEnabled = true;

                    if (!string.IsNullOrWhiteSpace(packagesBefore) && appVersion >= Constants.MinimumVersionForDeletePackages)
                    {
                        _output.WriteLine("Deleting packages BEFORE installation...");
                        
                        // Measure package deletion time BEFORE
                        _metricsService.Measure("packages_delete_before", () =>
                        {
                            ExecutePreparerAction(() => preparer.DeletePackages(sitePath, packagesBefore), "[ERROR] Deleting packages failed.", cancellationToken);
                            ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                            ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                        });
                        _metricsService.IncrementCounter("packages_deleted_before");
                    }

                    if (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath))
                    {
                        _output.WriteLine("Start installation packages...");
                        
                        // Measure package installation time
                        _metricsService.Measure("package_install", () =>
                        {
                            ExecutePreparerAction(() => preparer.InstallFromRepository(sitePath, packagesPath), "[ERROR] Failed to install packages.", cancellationToken);
                            ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                            ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                        });
                        _metricsService.IncrementCounter("packages_installed_count");
                    }

                    if (!string.IsNullOrWhiteSpace(packagesAfter) && appVersion >= Constants.MinimumVersionForDeletePackages)
                    {
                        _output.WriteLine("Deleting packages AFTER installation...");
                        
                        // Measure package deletion time AFTER
                        _metricsService.Measure("packages_delete_after", () =>
                        {
                            ExecutePreparerAction(() => preparer.DeletePackages(sitePath, packagesAfter), "[ERROR] Deleting packages failed.", cancellationToken);
                            ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                            ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                        });
                        _metricsService.IncrementCounter("packages_deleted_after");
                    }

                    if (OperatingSystem.IsWindows() && serverList.Length > 0 && viewModel.IsServerPanelVisible)
                    {
                        IsStopButtonEnabled = false;
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            // Measure server synchronization time
                            var syncStatus = _metricsService.Measure("server_sync", () =>
                            {
                                return _siteSynchronizer.SynchronizeAsync(sitePath, serverList.ToList(), cancellationToken).Result;
                            });

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
                        IsStopButtonEnabled = true;
                    }

                    if (string.IsNullOrWhiteSpace(packagesPath) &&
                        string.IsNullOrWhiteSpace(packagesBefore) &&
                        string.IsNullOrWhiteSpace(packagesAfter) &&
                        !viewModel.IsServerPanelVisible)
                    {
                        // Measure schema generation operations time
                        _metricsService.Measure("schema_operations", () =>
                        {
                            ExecutePreparerAction(() => preparer.RegenerateSchemaSources(sitePath), "[ERROR] Failed to regenerate schema sources.", cancellationToken);
                            ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken);
                            ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken);
                        });
                    }

                    var redisManager = _redisManagerFactory.Create(sitePath);
                    var redisStatus = redisManager.CheckStatus();
                    if (redisStatus)
                    {
                        redisManager.Clear();
                    }

                    IsStopButtonEnabled = false;

                    // Measure startup operation time
                    _metricsService.Measure("startup_operations", () =>
                    {
                        PerformStartupOperations(manager, localServerInfo, nestedPath, cancellationToken);
                    });
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

    private void PerformIisOperations(IRemoteIisManager manager, ServerInfo localServerInfo, string nestedPath, MainWindowViewModel viewModel, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(nestedPath))
            {
                if (!string.IsNullOrWhiteSpace(localServerInfo.PoolName))
                {
                    var stopPoolResult = manager.StopAppPoolAsync(localServerInfo.PoolName, cancellationToken).Result;
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
                    var stopSiteResult = manager.StopWebsiteAsync(localServerInfo.SiteName, cancellationToken).Result;
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
            var serviceStopResult = manager.StopServiceAsync(localServerInfo.ServiceName, cancellationToken).Result;
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

    private void PerformStartupOperations(IRemoteIisManager manager, ServerInfo localServerInfo, string nestedPath, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(nestedPath))
            {
                if (!string.IsNullOrWhiteSpace(localServerInfo.PoolName)) 
                {
                    var startPoolResult = manager.StartAppPoolAsync(localServerInfo.PoolName, cancellationToken).Result;
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
                    var startSiteResult = manager.StartWebsiteAsync(localServerInfo.SiteName, cancellationToken).Result;
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
                var serviceStartResult = manager.StartServiceAsync(localServerInfo.ServiceName, cancellationToken).Result;
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
                var serviceStartResult = manager.StartServiceAsync(localServerInfo.ServiceName, cancellationToken).Result;
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
}
