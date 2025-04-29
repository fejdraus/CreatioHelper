using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CreatioHelper.Core;
using CreatioHelper.Core.Services;
using CreatioHelper.ViewModels;

namespace CreatioHelper;

public partial class MainWindow : Window
{
    private bool _isBusy;
    private CancellationTokenSource? _cancellationTokenSource;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        Closing += OnMainWindowClosing;
    }

    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isBusy)
        {
            e.Cancel = true;
            var warningWindow = new CloseWarningWindow
            {
                Title = "Attention",
                Icon = Icon,
            };
            await warningWindow.ShowDialog(this);
        }
    }

    private void SetControlsEnabled(bool isEnabled)
    {
        ServerPanelStack.IsEnabled = isEnabled;
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
        StopButtonAndKillWorkspaceConsole.IsEnabled = !isEnabled;
    }
    
    private async Task StartButton_ClickAsync()
    {
        OutputTextBox.Text = "";

        if (DataContext is not MainWindowViewModel viewModel)
        {
            OutputTextBox.Text = "Unable to resolve DataContext.";
            return;
        }

        if (!TryValidateInputs(viewModel, out var sitePath, out var siteName))
            return;

        string packagesPath = PackagesPathTextBox.Text ?? "";
        string packagesBefore = PackagesToDeleteBeforeTextBox.Text?.Trim() ?? "";
        string packagesAfter = PackagesToDeleteAfterTextBox.Text?.Trim() ?? "";
        var serverList = viewModel.ServerList.ToArray();
        
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        var writer = new BufferingOutputWriter(line =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                OutputTextBox.Text += line + Environment.NewLine;
                OutputTextBox.CaretIndex = OutputTextBox.Text.Length;
                LogScrollViewer.Offset = new Vector(0, LogScrollViewer.Extent.Height);
            });
        });

        var preparer = new WorkspacePreparer(writer);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _isBusy = true;
            StartButton.Content = "In process...";
            SetControlsEnabled(false);
        });

        await Task.Run(async () =>
        {
            try
            {
                writer.WriteLine("Prepare WorkspaceConsole ...");
                preparer.Prepare(sitePath);
                
                if (cancellationToken.IsCancellationRequested)
                    return;
                
                if (sitePath != null && siteName != null)
                {
                    RemoteIisManager? manager = null;
                    if (OperatingSystem.IsWindows())
                    {
                        manager = new RemoteIisManager(Environment.MachineName, writer);
                        if(!await manager.StopAppPoolAsync(siteName) || !await manager.StopWebsiteAsync(siteName)) return;
                    }
                    
                    if (!string.IsNullOrWhiteSpace(packagesBefore))
                    {
                        writer.WriteLine("Deleting packages BEFORE installation...");
                        if (!cancellationToken.IsCancellationRequested) preparer.DeletePackages(sitePath, packagesBefore);
                        if (!cancellationToken.IsCancellationRequested) preparer.RebuildWorkspace(sitePath);
                        if (!cancellationToken.IsCancellationRequested) preparer.BuildConfiguration(sitePath);
                    }
                    
                    if (!string.IsNullOrWhiteSpace(packagesPath) && Directory.Exists(packagesPath))
                    {
                        writer.WriteLine("Start installation packages...");
                        if (!cancellationToken.IsCancellationRequested) preparer.InstallFromRepository(sitePath, packagesPath);
                        if (!cancellationToken.IsCancellationRequested) preparer.RebuildWorkspace(sitePath);
                        if (!cancellationToken.IsCancellationRequested) preparer.BuildConfiguration(sitePath);
                    }

                    if (!string.IsNullOrWhiteSpace(packagesAfter))
                    {
                        writer.WriteLine("Deleting packages AFTER installation...");
                        if (!cancellationToken.IsCancellationRequested) preparer.DeletePackages(sitePath, packagesAfter);
                        if (!cancellationToken.IsCancellationRequested) preparer.RebuildWorkspace(sitePath);
                        if (!cancellationToken.IsCancellationRequested) preparer.BuildConfiguration(sitePath);
                    }

                    if (OperatingSystem.IsWindows() && serverList.Length > 0 && viewModel.IsServerPanelVisible)
                    {
                        var syncService = new RemoteSynchronizationService(writer);
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            var syncStatus = await syncService.SynchronizeAsync(siteName, sitePath, serverList.ToList());
                            writer.WriteLine(syncStatus
                                ? "[OK] All servers are successfully synchronized."
                                : "[ERROR] Failed to synchronize servers.");
                        }
                    }

                    if (string.IsNullOrWhiteSpace(packagesPath) &&
                        string.IsNullOrWhiteSpace(packagesBefore) &&
                        string.IsNullOrWhiteSpace(packagesAfter) &&
                        !viewModel.IsServerPanelVisible)
                    {
                        if (!cancellationToken.IsCancellationRequested) preparer.RebuildWorkspace(sitePath);
                        if (!cancellationToken.IsCancellationRequested) preparer.BuildConfiguration(sitePath);
                    }
                    
                    if (OperatingSystem.IsWindows() && manager != null)
                    {
                        await manager.StartAppPoolAsync(siteName);
                        await manager.StartWebsiteAsync(siteName);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    OutputTextBox.Text += "[INFO] Operation was cancelled." + Environment.NewLine;
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    OutputTextBox.Text += $"[ERROR] {ex.Message}" + Environment.NewLine;
                });
            }
        }).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _isBusy = false;
                StartButton.Content = "Start";
                SetControlsEnabled(true);
            });
        });
    }

    private bool TryValidateInputs(MainWindowViewModel viewModel, out string? sitePath, out string? siteName)
    {
        sitePath = viewModel.IsIisMode
            ? viewModel.SelectedIisSite?.Path
            : SitePathTextBox.Text;
        siteName = viewModel.IsIisMode
            ? viewModel.SelectedIisSite?.Name
            : Path.GetFileName(sitePath);

        if (string.IsNullOrWhiteSpace(sitePath))
        {
            OutputTextBox.Text += "The path to the site is not indicated.\n";
            return false;
        }

        if (string.IsNullOrWhiteSpace(siteName))
        {
            OutputTextBox.Text += "Failed to get the site name.\n";
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

        if (folders.Count > 0)
        {
            var path = folders[0].Path.LocalPath;
            SitePathTextBox.Text = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
            var path = folders[0].Path.LocalPath;
            PackagesPathTextBox.Text = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
                // показать предупреждение при необходимости
            }
        }
    }

    private async void ServerName_DoubleClick(object? sender, PointerPressedEventArgs e)
    {
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

            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
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
        // Cancel the ongoing operation
        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
            OutputTextBox.Text += "[INFO] Cancelling operations..." + Environment.NewLine;
        }
        
        // Kill any running WorkspaceConsole processes
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var processes = Process.GetProcessesByName("Terrasoft.Tools.WorkspaceConsole");
                if (processes.Length > 0)
                {
                    foreach (var process in processes)
                    {
                        process.Kill();
                    }
                    OutputTextBox.Text += $"[INFO] Terminated {processes.Length} WorkspaceConsole processes." + Environment.NewLine;
                }
                else
                {
                    OutputTextBox.Text += "[INFO] No WorkspaceConsole processes found to terminate." + Environment.NewLine;
                }
            }
            catch (Exception ex)
            {
                OutputTextBox.Text += $"[ERROR] Failed to terminate WorkspaceConsole processes: {ex.Message}" + Environment.NewLine;
            }
        }
        
        // Reset UI state if needed
        _isBusy = false;
        StartButton.Content = "Start";
        SetControlsEnabled(true);
    }
}