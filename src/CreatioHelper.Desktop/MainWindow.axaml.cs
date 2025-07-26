using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Core;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Core.Services;
using CreatioHelper.Converters;
using CreatioHelper.Services;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Mediator;
using CreatioHelper.ViewModels;
using Microsoft.Extensions.DependencyInjection;

using System.Runtime.Versioning;

namespace CreatioHelper
{
    [SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
        private readonly BufferingOutputWriter _writer;
        private readonly MainWindowViewModel _viewModel;
        private const string LogFilePath = "log.txt";

        public MainWindow()
        {
            InitializeComponent();
            LogTextEditor.TextArea.TextView.LineTransformers.Add(
                new LogLineColorizer()
            );
            _writer = new BufferingOutputWriter(
                line =>
                {
                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        AddLogTextLine(line);
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() => { AddLogTextLine(line); });
                    }
                },
                () =>
                {
                    if (Dispatcher.UIThread.CheckAccess())
                    {
                        ClearLog();
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(ClearLog);
                    }
                });

            var provider = App.Services ?? throw new InvalidOperationException("Service provider not initialized");
            var mediator = provider.GetRequiredService<IMediator>();
            var systemServiceManager = provider.GetRequiredService<ISystemServiceManager>();
            var remoteManager = new RemoteIisManager(_writer);
            var statusService = new ServerStatusService(_writer, remoteManager);
            var dialogService = new DialogService(StorageProvider);
            var operationsService = new OperationsService(_writer, remoteManager);
            var iisService = new IisService();
            _viewModel = new MainWindowViewModel(_writer, mediator, operationsService, dialogService, statusService, remoteManager, iisService, systemServiceManager);
            DataContext = _viewModel;
            SitePathTextBox.TextChanged += SitePathTextBox_TextChanged;
            Closing += OnMainWindowClosing;
        }

        private void AddLogTextLine(string line)
        {
            var doc = LogTextEditor.Document;
            var nl = doc.TextLength > 0 ? Environment.NewLine : "";
            doc.Insert(doc.TextLength, nl + line);
            const int maxLines = 1000;
            const int removeBatch = 200;
            if (doc.LineCount > maxLines)
            {
                var first = doc.GetLineByNumber(1);
                var last = doc.GetLineByNumber(removeBatch);
                int lengthToRemove = (last.Offset + last.TotalLength) - first.Offset;
                doc.Remove(first.Offset, lengthToRemove);
            }
            var updateLogDisplay = new UpdateLogDisplay();
            updateLogDisplay.ScrollToBottom(_viewModel.IsAutoScrollEnabled, _viewModel.IsWrapTextEnabled, LogTextEditor);
            if (_viewModel.IsLogToFileEnabled)
            {
                AppendLogToFile(line);
            }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            SetupLogTextEditorContextMenu();
        }

        private void SetupLogTextEditorContextMenu()
        {
            if (LogTextEditor != null)
            {
                var contextMenu = new ContextMenu();
                
                var copyMenuItem = new MenuItem
                {
                    Header = "Copy"
                };
                copyMenuItem.Click += (_, _) => 
                {
                    if (LogTextEditor.TextArea.Selection.Length > 0)
                    {
                        LogTextEditor.Copy();
                    }
                };
                var clearLogMenuItem = new MenuItem
                {
                    Header = "Clear Log"
                };
                clearLogMenuItem.Click += (_, _) => 
                {
                    if (LogTextEditor.Document != null)
                    {
                        ClearLog();
                    }
                };
                contextMenu.Items.Add(copyMenuItem);
                contextMenu.Items.Add(new Separator());
                contextMenu.Items.Add(clearLogMenuItem);
                LogTextEditor.ContextMenu = contextMenu;
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

        protected virtual void ClearLog()
        {
            LogTextEditor.Text = string.Empty;
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
        
        private void AppendLogToFile(string logEntry)
        {
            try
            {
                File.AppendAllText(LogFilePath, string.Concat(DateTime.Now, " ", logEntry) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _writer.WriteLine($"[ERROR] Failed to write to log file: {ex.Message}");
            }
        }
    }
}
