using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Utils;
using CreatioHelper.ViewModels;

namespace CreatioHelper.Services;

[SupportedOSPlatform("windows")]
public partial class OperationsService : ObservableObject, IOperationsService
{
    private readonly IOutputWriter _output;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly IRemoteIisManager _remoteIisManager;
    private readonly ISiteSynchronizer _siteSynchronizer;
    private readonly IWorkspacePreparer _workspacePreparer;
    private readonly IRedisManagerFactory _redisManagerFactory;

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
        IRedisManagerFactory redisManagerFactory)
    {
        _output = output;
        _remoteIisManager = remoteIisManager;
        _siteSynchronizer = siteSynchronizer;
        _workspacePreparer = workspacePreparer;
        _redisManagerFactory = redisManagerFactory;
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

        string packagesPath = viewModel.PackagesPath ?? "";
        string packagesBefore = viewModel.PackagesToDeleteBefore?.Trim() ?? "";
        string packagesAfter = viewModel.PackagesToDeleteAfter?.Trim() ?? "";
        var serverList = viewModel.ServerList.ToArray();
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;
        var preparer = _workspacePreparer;
        IsBusy = true;
        StartButtonText = "In process...";
        viewModel.IsServerControlsEnabled = false;

        await Task.Run(async () => 
        {
            var quartzIsActiveOriginal = true;
            try 
            {
                _output.WriteLine("Prepare WorkspaceConsole ...");
                preparer.Prepare(sitePath, out quartzIsActiveOriginal);

                if (cancellationToken.IsCancellationRequested) 
                {
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
                        Name = Environment.MachineName,
                        PoolName = poolName ?? string.Empty,
                        SiteName = siteName ?? string.Empty,
                        AppVersion = appVersion
                    };
                    var manager = _remoteIisManager;
                    if (OperatingSystem.IsWindows())
                    {
                        if (File.Exists(nestedPath))
                        {
                            if (!string.IsNullOrWhiteSpace(localServerInfo.PoolName))
                            {
                                await manager.StopAppPoolAsync(localServerInfo);
                                _output.WriteLine("[INFO] Main Pool stopped.");
                            }

                            if (!string.IsNullOrWhiteSpace(localServerInfo.SiteName))
                            {
                                await manager.StopWebsiteAsync(localServerInfo);
                                _output.WriteLine("[INFO] Main Website stopped.");
                            }
                        }
                        
                        if (!File.Exists(nestedPath) && !string.IsNullOrWhiteSpace(viewModel.ServiceName))
                        {
                            localServerInfo.ServiceName = viewModel.ServiceName;
                            var serviceStopResult = await manager.StopServiceAsync(localServerInfo);
                            if (serviceStopResult)
                            {
                                _output.WriteLine("[INFO] Main Service stopped.");
                            }
                            else
                            {
                                _output.WriteLine("[WARNING] Failed to stop main service or service was not running.");
                            }
                        }
                    }
                    else
                    {
                        if (!File.Exists(nestedPath) && !string.IsNullOrWhiteSpace(viewModel.ServiceName))
                        {
                            localServerInfo.ServiceName = viewModel.ServiceName;
                            var serviceStopResult = await manager.StopServiceAsync(localServerInfo);
                            if (serviceStopResult)
                            {
                                _output.WriteLine("[INFO] Main Service stopped.");
                            }
                            else
                            {
                                _output.WriteLine("[WARNING] Failed to stop main service or service was not running.");
                            }
                        }
                    }
                    
                    IsStopButtonEnabled = true;

                    if (!string.IsNullOrWhiteSpace(packagesBefore) && appVersion >= Constants.MinimumVersionForDeletePackages)
                    {
                        _output.WriteLine("Deleting packages BEFORE installation...");
                        if (!ExecutePreparerAction(() => preparer.DeletePackages(sitePath, packagesBefore), "[ERROR] Deleting packages failed.", cancellationToken)) return;
                        if (!ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken)) return;
                        if (!ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken)) return;
                    }

                    if (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath))
                    {
                        _output.WriteLine("Start installation packages...");
                        if (!ExecutePreparerAction(() => preparer.InstallFromRepository(sitePath, packagesPath), "[ERROR] Failed to install packages.", cancellationToken)) return;
                        if (!ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken)) return;
                        if (!ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken)) return;
                    }

                    if (!string.IsNullOrWhiteSpace(packagesAfter) && appVersion >= Constants.MinimumVersionForDeletePackages)
                    {
                        _output.WriteLine("Deleting packages AFTER installation...");
                        if (!ExecutePreparerAction(() => preparer.DeletePackages(sitePath, packagesAfter), "[ERROR] Deleting packages failed.", cancellationToken)) return;
                        if (!ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken)) return;
                        if (!ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken)) return;
                    }

                    if (OperatingSystem.IsWindows() && serverList.Length > 0 && viewModel.IsServerPanelVisible) 
                    {
                        IsStopButtonEnabled = false;
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            var syncStatus = await _siteSynchronizer.SynchronizeAsync(sitePath, serverList.ToList(), cancellationToken);
                            if (syncStatus) 
                            {
                                _output.WriteLine("[OK] All servers are successfully synchronized.");
                            } 
                            else 
                            {
                                _output.WriteLine("[ERROR] Failed to synchronize servers.");
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
                        if (!ExecutePreparerAction(() => preparer.RegenerateSchemaSources(sitePath), "[ERROR] Failed to regenerate schema sources.", cancellationToken)) return;
                        if (!ExecutePreparerAction(() => preparer.RebuildWorkspace(sitePath), "[ERROR] Rebuilding workspace failed.", cancellationToken)) return;
                        if (!ExecutePreparerAction(() => preparer.BuildConfiguration(sitePath), "[ERROR] Building configuration failed.", cancellationToken)) return;
                    }

                    var redisManager = _redisManagerFactory.Create(sitePath);
                    var redisStatus = redisManager.CheckStatus();
                    if (redisStatus) 
                    {
                        redisManager.Clear();
                    }

                    IsStopButtonEnabled = false;
                    if (OperatingSystem.IsWindows())
                    {
                        if (File.Exists(nestedPath))
                        {
                            if (!string.IsNullOrWhiteSpace(localServerInfo.PoolName)) 
                            {
                                await manager.StartAppPoolAsync(localServerInfo);
                                _output.WriteLine("[INFO] Main Pool is running.");
                            }
                            if (!string.IsNullOrWhiteSpace(localServerInfo.SiteName)) 
                            {
                                await manager.StartWebsiteAsync(localServerInfo);
                                _output.WriteLine("[INFO] Main Website is running.");
                            }
                        }

                        if (!File.Exists(nestedPath) && !string.IsNullOrWhiteSpace(localServerInfo.ServiceName))
                        {
                            var serviceStartResult = await manager.StartServiceAsync(localServerInfo);
                            if (serviceStartResult)
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
                            var serviceStartResult = await manager.StartServiceAsync(localServerInfo);
                            if (serviceStartResult)
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
            } 
            catch (OperationCanceledException) 
            {
                _output.WriteLine("[INFO] Operation was cancelled.");
                IsStopButtonEnabled = true;
            } 
            catch (Exception ex) 
            {
                _output.WriteLine($"[ERROR] {ex.Message}");
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
        }, _cancellationTokenSource.Token);
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
                foreach (ManagementObject obj in results)
                {
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
