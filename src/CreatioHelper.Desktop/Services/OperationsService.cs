using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Operations;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.ViewModels;

namespace CreatioHelper.Services;

public partial class OperationsService : ObservableObject, IOperationsService
{
    private readonly IOutputWriter _output;
    private readonly IDeploymentOrchestrator _orchestrator;
    private readonly IWorkspacePreparer _workspacePreparer;
    private ISyncthingMonitorService? _syncthingMonitor;
    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _startButtonText = "Start";

    [ObservableProperty]
    private bool _isStopButtonEnabled;

    public OperationsService(
        IOutputWriter output,
        IDeploymentOrchestrator orchestrator,
        IWorkspacePreparer workspacePreparer,
        ISyncthingMonitorService? syncthingMonitor = null)
    {
        _output = output;
        _orchestrator = orchestrator;
        _workspacePreparer = workspacePreparer;
        _syncthingMonitor = syncthingMonitor;
    }

    public async Task StartOperation(MainWindowViewModel viewModel, bool fullRebuild = true)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        var options = new DeploymentOptions
        {
            SitePath = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Path : viewModel.SitePath,
            SiteVersion = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Version : viewModel.SitePathWithVersion,
            IsIisMode = viewModel.IsIisMode,
            IisSiteName = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Name : null,
            IisPoolName = viewModel.IsIisMode ? viewModel.SelectedIisSite?.PoolName : null,
            IisPoolOnly = viewModel.IsIisMode && (viewModel.SelectedIisSite?.IsVirtualApp ?? false),
            ServiceName = viewModel.ServiceName,
            PackagesPath = viewModel.PackagesPath,
            PackagesToDeleteBefore = viewModel.PackagesToDeleteBefore,
            PackagesToDeleteAfter = viewModel.PackagesToDeleteAfter,
            PrevalidateBeforeInstall = viewModel.PrevalidateBeforeInstall,
            ResetUnlockedPackageFlags = viewModel.ResetUnlockedPackageFlags,
            Compile = fullRebuild ? CompileMode.Full : CompileMode.Incremental,
            Sync = viewModel.UseSyncthingForSync
                ? SyncMode.Syncthing
                : (viewModel.EnableFileCopySynchronization ? SyncMode.FileCopy : SyncMode.None),
            Servers = viewModel.ServerList.ToArray(),
            HasRemoteServers = viewModel.IsServerPanelVisible,
            SkipRedisClear = viewModel.SkipRedisClear,
            SkipServerRestart = viewModel.SkipServerRestart,
            SyncthingMonitor = _syncthingMonitor
        };

        var callbacks = new DesktopUiCallbacks(this, viewModel);
        await _orchestrator.RunAsync(options, callbacks, token).ConfigureAwait(false);
    }

    public async Task RestoreConfiguration(MainWindowViewModel viewModel)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        var options = new RestoreConfigurationOptions
        {
            SitePath = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Path : viewModel.SitePath,
            IsIisMode = viewModel.IsIisMode,
            IisSiteName = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Name : null,
            IisPoolName = viewModel.IsIisMode ? viewModel.SelectedIisSite?.PoolName : null,
            IisPoolOnly = viewModel.IsIisMode && (viewModel.SelectedIisSite?.IsVirtualApp ?? false),
            ServiceName = viewModel.ServiceName,
            Compile = CompileMode.Incremental,
            Servers = viewModel.ServerList.ToArray(),
            HasRemoteServers = viewModel.IsServerPanelVisible,
            SkipRedisClear = viewModel.SkipRedisClear
        };

        var callbacks = new DesktopUiCallbacks(this, viewModel);
        await _orchestrator.RestoreConfigurationAsync(options, callbacks, token).ConfigureAwait(false);
    }

    public async Task ExecuteWscOperationAsync(string sitePath, string operationName, Func<int> action, Func<bool>? preAction = null)
    {
        if (IsBusy)
        {
            _output.WriteLine("[WARN] Another operation is already running.");
            return;
        }

        _output.Clear();
        _cancellationTokenSource = new CancellationTokenSource();
        IsBusy = true;
        StartButtonText = "In process...";
        IsStopButtonEnabled = true;

        try
        {
            await Task.Run(() =>
            {
                if (preAction != null && !preAction())
                {
                    return;
                }

                _output.WriteLine($"Prepare WorkspaceConsole ...");
                _workspacePreparer.Prepare(sitePath, out _);

                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    _output.WriteLine("[INFO] Operation was cancelled.");
                    return;
                }

                _output.WriteLine($"Running {operationName}...");
                int result = action();
                if (result != 0)
                {
                    _output.WriteLine($"[ERROR] {operationName} failed.");
                }
                else
                {
                    _output.WriteLine($"[OK] {operationName} completed successfully.");
                }
            });
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            StartButtonText = "Start";
            IsStopButtonEnabled = false;
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

    public ISyncthingMonitorService? GetSyncthingMonitor() => _syncthingMonitor;

    public Task StartAllIisAsync(IEnumerable<ServerInfo> servers) => _orchestrator.StartAllIisAsync(servers);

    public Task StopAllIisAsync(IEnumerable<ServerInfo> servers) => _orchestrator.StopAllIisAsync(servers);

    public Task RestartAllIisAsync(IEnumerable<ServerInfo> servers)
    {
        if (IsBusy)
        {
            _output.WriteLine("[WARN] Another operation is already running.");
            return Task.CompletedTask;
        }

        var callbacks = new SelfUiCallbacks(this);
        return _orchestrator.RestartAllIisAsync(servers, callbacks);
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
        }

        return string.Empty;
    }

    private sealed class DesktopUiCallbacks : IDeploymentUiCallbacks
    {
        private readonly OperationsService _service;
        private readonly MainWindowViewModel _viewModel;

        public DesktopUiCallbacks(OperationsService service, MainWindowViewModel viewModel)
        {
            _service = service;
            _viewModel = viewModel;
        }

        public void OnBusyChanged(bool isBusy) => _service.IsBusy = isBusy;
        public void OnStopButtonEnabledChanged(bool enabled) => _service.IsStopButtonEnabled = enabled;
        public void OnStartButtonText(string text) => _service.StartButtonText = text;
        public void OnServerControlsEnabledChanged(bool enabled) => _viewModel.IsServerControlsEnabled = enabled;
    }

    private sealed class SelfUiCallbacks : IDeploymentUiCallbacks
    {
        private readonly OperationsService _service;

        public SelfUiCallbacks(OperationsService service)
        {
            _service = service;
        }

        public void OnBusyChanged(bool isBusy) => _service.IsBusy = isBusy;
        public void OnStopButtonEnabledChanged(bool enabled) => _service.IsStopButtonEnabled = enabled;
        public void OnStartButtonText(string text) => _service.StartButtonText = text;
        public void OnServerControlsEnabledChanged(bool enabled) { }
    }
}
