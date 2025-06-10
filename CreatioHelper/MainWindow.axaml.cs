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
using CreatioHelper.Core;
using CreatioHelper.Core.Services;
using CreatioHelper.ViewModels;
using System.ComponentModel;
using Avalonia.VisualTree;

namespace CreatioHelper
{
    public partial class MainWindow : Window
    {
        private bool _isBusy;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly BufferingOutputWriter _writer;
        private readonly MainWindowViewModel _viewModel;
        private string _logFilePath = "log.txt";

        public MainWindow()
        {
            InitializeComponent();
            _writer = new BufferingOutputWriter(line =>
            {
                _viewModel?.AddLogEntry(line);
                if (_viewModel?.IsLogToFileEnabled == true)
                {
                    AppendLogToFile(line);
                }
            });
            _viewModel = new MainWindowViewModel(_writer);
            DataContext = _viewModel;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            Closing += OnMainWindowClosing;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ShouldScrollToEnd))
            {
                string nl = Environment.NewLine;
                var allText = string.Join(nl, _viewModel.LogEntries);
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LogTextEditor.Text = allText;
                    if (_viewModel.IsAutoScrollEnabled)
                    {
                        var caretPos = 0;
                        if (_viewModel.IsWrapTextEnabled)
                        {
                            caretPos = LogTextEditor.Text.Length;
                            LogTextEditor.CaretOffset = caretPos;
                            int line   = LogTextEditor.TextArea.Caret.Line;
                            int column = LogTextEditor.TextArea.Caret.Column;
                            LogTextEditor.ScrollTo(line, column);
                        }
                        else
                        {
                            var lastNewlineIndex = allText.LastIndexOf(nl, StringComparison.Ordinal);
                            if (lastNewlineIndex >= 0)
                            {
                                caretPos = lastNewlineIndex + nl.Length;
                            }
                            LogTextEditor.CaretOffset = caretPos;
                            LogTextEditor.TextArea.Caret.BringCaretToView();
                        }
                        
                    }
                }, DispatcherPriority.Render);
            }
        }
        private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            if (!_isBusy) return;
            e.Cancel = true;
            var warningWindow = new CloseWarningWindow
            {
                Title = "Attention",
                Icon = Icon,
            };
            await warningWindow.ShowDialog(this);
        }

        private void SetControlsEnabled(bool isEnabled)
        {
            StartButton.IsEnabled = isEnabled;
            ServerPanelButton.IsEnabled = isEnabled;
            SiteSourcePanel.IsEnabled = isEnabled;
            IisSitesComboBox.IsEnabled = isEnabled;
            SitePathTextBox.IsEnabled = isEnabled;
            PackagesPathTextBox.IsEnabled = isEnabled;
            PackagesToDeleteBeforeTextBox.IsEnabled = isEnabled;
            PackagesToDeleteAfterTextBox.IsEnabled = isEnabled;
            BrowseSiteButton.IsEnabled = isEnabled;
            BrowsePackagesButton.IsEnabled = isEnabled;
            AddServerButton.IsEnabled = isEnabled;
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.IsServerControlsEnabled = isEnabled;
            }
        }

        private async Task StartButton_ClickAsync()
        {
            StopButtonAndKillWorkspaceConsole.IsEnabled = false;

            if (DataContext is not MainWindowViewModel viewModel)
            {
                _writer.WriteLine("Unable to resolve DataContext.");
                return;
            }

            // Очищаем лог перед новой операцией
            viewModel.ClearLog();

            if (!TryValidateInputs(viewModel, out var sitePath) || sitePath == null)
            {
                return;
            }
            string packagesPath = PackagesPathTextBox.Text ?? "";
            string packagesBefore = PackagesToDeleteBeforeTextBox.Text?.Trim() ?? "";
            string packagesAfter = PackagesToDeleteAfterTextBox.Text?.Trim() ?? "";
            var serverList = viewModel.ServerList.ToArray();
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;
            var preparer = new WorkspacePreparer(_writer);
            _isBusy = true;
            StartButton.Content = "In process...";
            SetControlsEnabled(false);

            await Task.Run(async () =>
            {
                var quartzIsActiveOriginal = true;
                try
                {
                    _writer.WriteLine("Prepare WorkspaceConsole ...");
                    preparer.Prepare(sitePath, out quartzIsActiveOriginal);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    if (OperatingSystem.IsWindows() && (viewModel.SelectedIisSite != null || !string.IsNullOrWhiteSpace(SitePathTextBox.Text)))
                    {
                        var poolName = viewModel.IsIisMode ? viewModel.SelectedIisSite?.PoolName : null;
                        var siteName = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Name : null;
                        var appVersion = viewModel.IsIisMode ? viewModel.SelectedIisSite?.Version : _viewModel.SitePathWithVersion;
                        if (appVersion < new Version(7, 12, 0, 0))
                        {
                            _writer.WriteLine("[ERROR] Creatio application not found.");
                            return;
                        }
                        var localServerInfo = new ServerInfo
                        {
                            Name = Environment.MachineName,
                            PoolName = poolName,
                            SiteName = siteName,
                            AppVersion = appVersion
                        };
                        var manager = new RemoteIisManager(_writer);
                        if (localServerInfo.PoolName != null)
                        {
                            await manager.StopAppPoolAsync(localServerInfo);
                            _writer.WriteLine("[INFO] Main Pool stopped.");
                        }
                        if (localServerInfo.SiteName != null)
                        {
                            await manager.StopWebsiteAsync(localServerInfo);
                            _writer.WriteLine("[INFO] Main Website stopped.");
                        }
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            StopButtonAndKillWorkspaceConsole.IsEnabled = true;
                        });

                        if (!string.IsNullOrWhiteSpace(packagesBefore) && appVersion >= Constants.MinimumVersionForDeletePackages)
                        {
                            _writer.WriteLine("Deleting packages BEFORE installation...");
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.DeletePackages(sitePath, packagesBefore);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Deleting packages failed.");
                                    return;
                                }
                            }
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.RebuildWorkspace(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Rebuilding workspace failed.");
                                    return;
                                }
                            }

                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.BuildConfiguration(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Building configuration failed.");
                                    return;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath))
                        {
                            _writer.WriteLine("Start installation packages...");
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.InstallFromRepository(sitePath, packagesPath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Failed to install packages.");
                                    return;
                                }
                            }
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.RebuildWorkspace(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Rebuilding workspace failed.");
                                    return;
                                }
                            }

                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.BuildConfiguration(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Building configuration failed.");
                                    return;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(packagesAfter) && appVersion >= Constants.MinimumVersionForDeletePackages)
                        {
                            _writer.WriteLine("Deleting packages AFTER installation...");
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.DeletePackages(sitePath, packagesAfter);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Deleting packages failed.");
                                    return;
                                }
                            }
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.RebuildWorkspace(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Rebuilding workspace failed.");
                                    return;
                                }
                            }

                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.BuildConfiguration(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Building configuration failed.");
                                    return;
                                }
                            }
                        }

                        if (OperatingSystem.IsWindows() && serverList.Length > 0 && viewModel.IsServerPanelVisible)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                StopButtonAndKillWorkspaceConsole.IsEnabled = false;
                            });
                            var syncService = new RemoteSynchronizationService(_writer);
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var syncStatus = await syncService.SynchronizeAsync(sitePath, serverList.ToList(),
                                    cancellationToken);
                                if (syncStatus)
                                {
                                    _writer.WriteLine("[OK] All servers are successfully synchronized.");
                                }
                                else
                                {
                                     _writer.WriteLine("[ERROR] Failed to synchronize servers.");
                                     return;
                                }
                            }
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                StopButtonAndKillWorkspaceConsole.IsEnabled = true;
                            });
                        }

                        if (string.IsNullOrWhiteSpace(packagesPath) &&
                            string.IsNullOrWhiteSpace(packagesBefore) &&
                            string.IsNullOrWhiteSpace(packagesAfter) &&
                            !viewModel.IsServerPanelVisible)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.RegenerateSchemaSources(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Failed to regenerate schema sources.");
                                    return;
                                }
                            }

                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.RebuildWorkspace(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Rebuilding workspace failed.");
                                    return;
                                }
                            }

                            if (!cancellationToken.IsCancellationRequested)
                            {
                                var result = preparer.BuildConfiguration(sitePath);
                                if (result != 0)
                                {
                                    _writer.WriteLine("[ERROR] Building configuration failed.");
                                    return;
                                }
                            }
                        }
                        
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            var redisManager = new RedisManager(_writer, sitePath);
                            var redisStatus = redisManager.CheckStatus();
                            if (redisStatus)
                            {
                                redisManager.Clear();
                            }
                        });

                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            StopButtonAndKillWorkspaceConsole.IsEnabled = false;
                        });
                        if (localServerInfo.PoolName != null)
                        {
                            await manager.StartAppPoolAsync(localServerInfo);
                            _writer.WriteLine("[INFO] Main Pool is running.");
                        }
                        if (localServerInfo.SiteName != null)
                        {
                            await manager.StartWebsiteAsync(localServerInfo);
                            _writer.WriteLine("[INFO] Main Website is running.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _writer.WriteLine("[INFO] Operation was cancelled.");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        StopButtonAndKillWorkspaceConsole.IsEnabled = true;
                    });
                }
                catch (Exception ex)
                {
                    _writer.WriteLine($"[ERROR] {ex.Message}");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        StopButtonAndKillWorkspaceConsole.IsEnabled = true;
                    });
                }
                finally
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _isBusy = false;
                        StartButton.Content = "Start";
                        SetControlsEnabled(true);
                        StopButtonAndKillWorkspaceConsole.IsEnabled = true;
                    });
                    if (!quartzIsActiveOriginal)
                    {
                        preparer.UpdateOutConfig(Path.Combine(sitePath, "Web.config"), quartzIsActiveOriginal);   
                    }
                }
            }, cancellationToken);
        }

        private bool TryValidateInputs(MainWindowViewModel viewModel, out string? sitePath)
        {
            sitePath = viewModel.IsIisMode
                ? viewModel.SelectedIisSite?.Path
                : SitePathTextBox.Text;

            if (string.IsNullOrWhiteSpace(sitePath))
            {
                _writer.WriteLine("The path to the site is not indicated.");
                return false;
            }

            return true;
        }

        private async void BrowseSitePath_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Site Path",
                AllowMultiple = false
            });
            if (folders.Count <= 0) return;
            var uri = folders[0].Path;
            if (uri.IsAbsoluteUri)
            {
                var path = uri.LocalPath;
                SitePathTextBox.Text = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var version = GetAppAssembly.GetAppVersion(path);
                if (version == new Version())
                {
                    _writer.WriteLine("[ERROR] Creatio application not found.");
                }
                _viewModel.SitePathWithVersion = version;
            }
            else
            {
                _writer.WriteLine("[ERROR] Selected path is not an absolute URI.");
            }
            
        }

        private async void BrowsePackagesPath_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Packages Path",
                AllowMultiple = false
            });
            if (folders.Count > 0)
            {
                var uri = folders[0].Path;
                if (uri.IsAbsoluteUri)
                {
                    var path = uri.LocalPath;
                    PackagesPathTextBox.Text = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                else
                {
                    _writer.WriteLine("[ERROR] Selected path is not an absolute URI.");
                }
            }
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

        private async void StartButton_Click(object? sender, RoutedEventArgs e)
        {
            await StartButton_ClickAsync();
        }

        private void StopButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                _writer.WriteLine("[INFO] Cancelling operations...");
            }
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var processes = Process.GetProcessesByName("Terrasoft.Tools.WorkspaceConsole");
                    if (processes.Length > 0)
                    {
                        foreach (var process in processes)
                            process.Kill();
                        _writer.WriteLine($"[INFO] Terminated {processes.Length} WorkspaceConsole processes.");
                    }
                    else
                    {
                        _writer.WriteLine("[INFO] No WorkspaceConsole processes found to terminate.");
                    }
                }
                catch (Exception ex)
                {
                    _writer.WriteLine($"[ERROR] Failed to terminate WorkspaceConsole processes: {ex.Message}");
                }
            }
            _isBusy = false;
            StartButton.Content = "Start";
            SetControlsEnabled(true);
        }
        
        private void AppendLogToFile(string logEntry)
        {
            try
            {
                File.AppendAllText(_logFilePath, string.Concat(DateTime.Now, " ", logEntry) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _writer.WriteLine($"[ERROR] Failed to write to log file: {ex.Message}");
            }
        }
    }
}
