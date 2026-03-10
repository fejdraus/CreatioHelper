using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Utils;
using CreatioHelper.Converters;
using CreatioHelper.Services;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Mediator;
using CreatioHelper.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Workspace;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Logging;

namespace CreatioHelper
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel? _viewModel;
        private const string LogFilePath = "log.txt";

        public MainWindow()
        {
            InitializeComponent();

            if (Design.IsDesignMode)
                return;

            LogTextEditor.TextArea.TextView.LineTransformers.Add(
                new LogLineColorizer()
            );

            var provider = App.Services ?? throw new InvalidOperationException("Service provider not initialized");
            var writer = provider.GetRequiredService<IOutputWriter>();
            var logDisplayHelper = new UpdateLogDisplay();
            OutputWriterHandlers.WriteAction = line =>
            {
                void Append()
                {
                    LogTextEditor.AppendText(line + Environment.NewLine);
                    logDisplayHelper.ScrollToBottom(_viewModel != null && _viewModel.IsAutoScrollEnabled, _viewModel != null && _viewModel.IsWrapTextEnabled, LogTextEditor);
                    FileLogService.AppendLine(line);
                }

                if (Dispatcher.UIThread.CheckAccess())
                {
                    Append();
                }
                else
                {
                    Dispatcher.UIThread.Post(Append);
                }
            };
            OutputWriterHandlers.ClearAction = () =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    LogTextEditor.Text = string.Empty;
                }
                else
                {
                    Dispatcher.UIThread.Post(() => { LogTextEditor.Text = string.Empty; });
                }

                FileLogService.Clear();
            };

            
            var mediator = provider.GetRequiredService<IMediator>();
            var systemServiceManager = provider.GetRequiredService<ISystemServiceManager>();
            var iisManager = provider.GetRequiredService<IIisManager>();
            // remoteManager no longer needed - using iisManager directly
            var metricsService = provider.GetRequiredService<IMetricsService>();
            var statusService = provider.GetRequiredService<IServerStatusService>();
            
            var dialogService = new DialogService(StorageProvider);
            var siteSync = provider.GetRequiredService<ISiteSynchronizer>();
            var workspacePreparer = new WorkspacePreparer(writer);
            var redisFactory = provider.GetRequiredService<IRedisManagerFactory>();

            // SyncthingMonitorService will be created dynamically when needed
            var operationsService = new OperationsService(writer, iisManager, siteSync, workspacePreparer, redisFactory, metricsService, statusService);
            var iisService = new IisService();
            _viewModel = new MainWindowViewModel(writer, mediator, operationsService, dialogService, statusService, iisManager, iisService, systemServiceManager, redisFactory, workspacePreparer);

            // Monitor for Syncthing configuration changes and create/update SyncthingMonitorService
            _viewModel.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.UseSyncthingForSync) ||
                    args.PropertyName == nameof(MainWindowViewModel.SyncthingApiUrl) ||
                    args.PropertyName == nameof(MainWindowViewModel.SyncthingApiKey))
                {
                    await UpdateSyncthingMonitor(operationsService, statusService, provider, writer);
                }
            };
            DataContext = _viewModel;
            FileLogService.LogFilePath = LogFilePath;
            FileLogService.Enabled = _viewModel.IsLogToFileEnabled;
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsLogToFileEnabled))
                {
                    FileLogService.Enabled = _viewModel.IsLogToFileEnabled;
                }
            };
            SitePathTextBox.TextChanged += SitePathTextBox_TextChanged;
            Closing += OnMainWindowClosing;
            Closed += OnMainWindowClosed;
        }

        private async void OnMainWindowClosed(object? sender, EventArgs e)
        {
            // Flush all pending logs before exit
            try
            {
                await FileLogService.FlushAsync();
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_viewModel is { IsBusy: false }) return;
            e.Cancel = true;
            var warningWindow = new CloseWarningWindow
            {
                Title = "Attention",
                Icon = Icon,
            };
            await warningWindow.ShowDialog(this);
        }

        private async void AddServer_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var window = new AddServerWindow(null, vm.UseSyncthingForSync, vm.EnableFileCopySynchronization);
            var newServer = await window.ShowDialog<ServerInfo?>(this);

            if (newServer != null)
            {
                if (vm.ServerList.All(s => s.Name != newServer.Name))
                {
                    vm.ServerList.Add(newServer);
                }
                else
                {
                    // ...
                }
            }
        }

        private async void SyncthingSettings_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var settingsWindow = new SyncthingSettingsWindow(
                vm.EnableFileCopySynchronization,
                vm.UseSyncthingForSync,
                vm.SyncthingApiUrl,
                vm.SyncthingApiKey);

            var result = await settingsWindow.ShowDialog<SyncthingSettingsResult?>(this);

            if (result != null)
            {
                vm.EnableFileCopySynchronization = result.EnableFileCopySynchronization;
                vm.UseSyncthingForSync = result.UseSyncthingForSync;
                vm.SyncthingApiUrl = result.SyncthingApiUrl;
                vm.SyncthingApiKey = result.SyncthingApiKey;
            }
        }

        private void OpenSyncthingWeb_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (string.IsNullOrEmpty(vm.SyncthingApiUrl))
            {
                return;
            }

            try
            {
                // Open URL in default browser
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vm.SyncthingApiUrl,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                // If opening fails, show error in log
                var writer = App.Services?.GetService<IOutputWriter>();
                writer?.WriteLine($"[ERROR] Failed to open Syncthing Web UI: {ex.Message}");
            }
        }

        private async void SaveLicenseRequest_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            var dialog = new SaveLicenseRequestWindow();
            var result = await dialog.ShowDialog<SaveLicenseRequestResult?>(this);

            if (result != null)
            {
                await vm.ExecuteSaveLicenseRequest(result.CustomerId, result.FilePath);
            }
        }

        private async void ServerName_DoubleClick(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm || !vm.IsServerControlsEnabled)
                return;

            if (e.ClickCount == 2 && sender is TextBlock { DataContext: ServerInfo server })
            {
                var clone = new ServerInfo
                {
                    Name = server.Name,
                    NetworkPath = server.NetworkPath,
                    SiteName = server.SiteName,
                    PoolName = server.PoolName,
                    SyncthingDeviceId = server.SyncthingDeviceId,
                    SyncthingFolderIds = new List<string>(server.SyncthingFolderIds)
                };
                var editWindow = new AddServerWindow(clone, vm.UseSyncthingForSync, vm.EnableFileCopySynchronization);
                var updated = await editWindow.ShowDialog<ServerInfo?>(this);
                if (updated == null) return;
                server.Name = updated.Name;
                server.NetworkPath = updated.NetworkPath;
                server.SiteName = updated.SiteName;
                server.PoolName = updated.PoolName;
                server.SyncthingDeviceId = updated.SyncthingDeviceId;
                server.SyncthingFolderIds = new List<string>(updated.SyncthingFolderIds);
                // Clean up folder sync states that are no longer in the folder list
                server.PruneStaleFolderStates();
            }
        }

        private void SitePathTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                try
                {
                    var path = textBox.Text.Trim();
                    if (Directory.Exists(path))
                    {
                        var version = AppVersionHelper.GetAppVersion(path);
                        _viewModel?.SitePathWithVersion = version;
                    }
                }
                catch
                {
                    _viewModel?.SitePathWithVersion = new Version();
                }
            }
            else
            {
                _viewModel?.SitePathWithVersion = new Version();
            }
        }

        private System.Threading.Tasks.Task UpdateSyncthingMonitor(
            OperationsService operationsService,
            IServerStatusService statusService,
            IServiceProvider provider,
            IOutputWriter writer)
        {
            if (_viewModel is { UseSyncthingForSync: true } &&
                !string.IsNullOrEmpty(_viewModel.SyncthingApiUrl) &&
                !string.IsNullOrEmpty(_viewModel.SyncthingApiKey))
            {
                try
                {
                    var httpClientFactory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
                    var monitor = new SyncthingMonitorService(
                        httpClientFactory,
                        writer,
                        _viewModel.SyncthingApiUrl,
                        _viewModel.SyncthingApiKey);

                    // Use reflection to update the private field in OperationsService
                    var field = typeof(OperationsService).GetField("_syncthingMonitor",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    field?.SetValue(operationsService, monitor);

                    // Update ServerStatusService to use the monitor
                    if (statusService is ServerStatusService concreteStatusService)
                    {
                        concreteStatusService.SetSyncthingMonitor(monitor);
                    }
                }
                catch (Exception ex)
                {
                    writer.WriteLine($"[ERROR] Failed to configure Syncthing monitor: {ex.Message}");
                }
            }
            else
            {
                // Clear Syncthing monitor when disabled
                var field = typeof(OperationsService).GetField("_syncthingMonitor",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field?.SetValue(operationsService, null);

                // Clear monitor from StatusService
                if (statusService is ServerStatusService concreteStatusService)
                {
                    concreteStatusService.SetSyncthingMonitor(null);
                }
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
