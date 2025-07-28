using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Shared.Utils;
using CreatioHelper.Converters;
using CreatioHelper.Services;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Mediator;
using CreatioHelper.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using CreatioHelper.Infrastructure.Services.Workspace;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Logging;
using AvaloniaEdit;

namespace CreatioHelper
{
    public partial class MainWindow : Window
    {
        private readonly IOutputWriter _writer;
        private readonly MainWindowViewModel _viewModel;
        private const string LogFilePath = "log.txt";

        public MainWindow()
        {
            InitializeComponent();
            LogTextEditor.TextArea.TextView.LineTransformers.Add(
                new LogLineColorizer()
            );

            var provider = App.Services ?? throw new InvalidOperationException("Service provider not initialized");
            _writer = provider.GetRequiredService<IOutputWriter>();
            var logDisplayHelper = new UpdateLogDisplay();
            OutputWriterHandlers.WriteAction = line =>
            {
                void append()
                {
                    LogTextEditor.AppendText(line + Environment.NewLine);
                    logDisplayHelper.ScrollToBottom(_viewModel.IsAutoScrollEnabled, _viewModel.IsWrapTextEnabled, LogTextEditor);
                }

                if (Dispatcher.UIThread.CheckAccess())
                {
                    append();
                }
                else
                {
                    Dispatcher.UIThread.Post(append);
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
            };

            
            var mediator = provider.GetRequiredService<IMediator>();
            var systemServiceManager = provider.GetRequiredService<ISystemServiceManager>();
            var remoteManager = provider.GetRequiredService<IRemoteIisManager>();
            var metricsService = provider.GetRequiredService<IMetricsService>();
            var statusService = provider.GetRequiredService<IServerStatusService>();
            
            var dialogService = new DialogService(StorageProvider);
            var siteSync = provider.GetRequiredService<ISiteSynchronizer>();
            var workspacePreparer = new WorkspacePreparer(_writer);
            var redisFactory = provider.GetRequiredService<IRedisManagerFactory>();
            var operationsService = new OperationsService(_writer, remoteManager, siteSync, workspacePreparer, redisFactory, metricsService);
            var iisService = new IisService();
            _viewModel = new MainWindowViewModel(_writer, mediator, operationsService, dialogService, statusService, remoteManager, iisService, systemServiceManager);
            DataContext = _viewModel;
            SitePathTextBox.TextChanged += SitePathTextBox_TextChanged;
            Closing += OnMainWindowClosing;
        }

        private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            if (!_viewModel.IsBusy) return;
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
            var window = new AddServerWindow();
            var newServer = await window.ShowDialog<ServerInfo?>(this);

            if (newServer != null && DataContext is MainWindowViewModel vm)
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

        private async void ServerName_DoubleClick(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm && !vm.IsServerControlsEnabled)
                return;  
            if (e.ClickCount == 2 && sender is TextBlock { DataContext: ServerInfo server })
            {
                var clone = new ServerInfo
                {
                    Name = server.Name,
                    NetworkPath = server.NetworkPath,
                    SiteName = server.SiteName,
                    PoolName = server.PoolName
                };
                var editWindow = new AddServerWindow(clone);
                var updated = await editWindow.ShowDialog<ServerInfo?>(this);
                if (updated == null) return;
                server.Name = updated.Name;
                server.NetworkPath = updated.NetworkPath;
                server.SiteName = updated.SiteName;
                server.PoolName = updated.PoolName;
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
                        _viewModel.SitePathWithVersion = version;
                    }
                }
                catch
                {
                    _viewModel.SitePathWithVersion = new Version();
                }
            }
            else
            {
                _viewModel.SitePathWithVersion = new Version();
            }
        }
    }
}
