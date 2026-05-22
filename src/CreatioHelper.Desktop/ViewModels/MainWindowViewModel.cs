using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Models;
using CreatioHelper.Services;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Application.Mediator;
using CreatioHelper.Application.Settings;
using System.Threading;
using CreatioHelper.Domain.ValueObjects;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Text.Json;
using Avalonia.Threading;
using Version = System.Version;

namespace CreatioHelper.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private bool _isInitializing;
    private bool _settingsLoaded = false;
    private readonly Dictionary<ServerInfo, PropertyChangedEventHandler> _serverHandlers = new();
    private readonly IServerStatusService _statusService;
    private readonly IIisManager _iisManager;
    private readonly IisService _iisService;
    private readonly IMediator _mediator;
    private readonly IOperationsService _operationsService;
    private readonly IDialogService _dialogService;
    private readonly ISystemServiceManager _systemServiceManager;
    private readonly IRedisManagerFactory _redisManagerFactory;
    private readonly IWorkspacePreparer _workspacePreparer;
    private readonly IPackageCleaner _packageCleaner;
    private Version _sitePathWithVersion = new();
    private readonly IOutputWriter _output;
    private readonly IMetricsService _metricsService;

    public MainWindowViewModel(
        IOutputWriter output,
        IMediator mediator,
        IOperationsService operationsService,
        IDialogService dialogService,
        IServerStatusService statusService,
        IIisManager iisManager,
        IisService iisService,
        ISystemServiceManager systemServiceManager,
        IRedisManagerFactory redisManagerFactory,
        IWorkspacePreparer workspacePreparer,
        IPackageCleaner packageCleaner,
        IMetricsService metricsService)
    {
        _output = output;
        _metricsService = metricsService;
        _mediator = mediator;
        _operationsService = operationsService;
        _operationsService.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IOperationsService.IsBusy))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(AreControlsEnabled));
                OnPropertyChanged(nameof(CanRestartLocalIis));
                if (!_operationsService.IsBusy)
                {
                    _ = RefreshMetricsAsync();
                }
            }
            else if (args.PropertyName == nameof(IOperationsService.StartButtonText))
                OnPropertyChanged(nameof(StartButtonText));
            else if (args.PropertyName == nameof(IOperationsService.IsStopButtonEnabled))
                OnPropertyChanged(nameof(IsStopButtonEnabled));
        };
        _dialogService = dialogService;
        _statusService = statusService;
        _isInitializing = true;
        _iisManager = iisManager;
        _iisService = iisService;
        _systemServiceManager = systemServiceManager;
        _redisManagerFactory = redisManagerFactory;
        _workspacePreparer = workspacePreparer;
        _packageCleaner = packageCleaner;

        // Initialize Syncthing commands
        ResumeAllSyncthingFoldersCommand = new AsyncRelayCommand(ResumeAllSyncthingFolders);
        PauseAllSyncthingFoldersCommand = new AsyncRelayCommand(PauseAllSyncthingFolders);

        // Initialize IIS bulk commands
        StartAllIisCommand = new AsyncRelayCommand(StartAllIis);
        StopAllIisCommand = new AsyncRelayCommand(StopAllIis);

        // Subscribe to invalid sync data events for diagnostics
        ServerInfo.OnInvalidSyncDataReceived += (serverName, folderId, needBytes, needItems, completion) =>
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var message = $"[{timestamp}] WARNING: Invalid sync data received - Server: {serverName}, Folder: {folderId}, NeedBytes: {needBytes}, NeedItems: {needItems}, Completion: {completion:F2}%";
            FileLogService.AppendLine(message);
            _output.WriteLine(message);
        };

        _metricsService.MetricsUpdated += async (_, _) =>
            await Dispatcher.UIThread.InvokeAsync(RefreshMetricsAsync);

        if (_output is INotifyCleared notifyCleared)
        {
            notifyCleared.Cleared += () => Dispatcher.UIThread.InvokeAsync(ClearCharts);
        }

        // Initialize asynchronously after construction
        _ = InitializeAsync();

        foreach (var server in ServerList)
        {
            var handler = new PropertyChangedEventHandler((_, args) =>
            {
                SaveServerSettings();
                // Notify when IIS properties change
                if (args.PropertyName == nameof(ServerInfo.PoolName) || args.PropertyName == nameof(ServerInfo.SiteName))
                {
                    OnPropertyChanged(nameof(CanUseIisBulkOperations));
                }
            });
            _serverHandlers[server] = handler;
            server.PropertyChanged += handler;
        }

        ServerList.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (ServerInfo item in e.NewItems)
                {
                    var handler = new PropertyChangedEventHandler((_, args) =>
                    {
                        SaveServerSettings();
                        // Notify when IIS properties change
                        if (args.PropertyName == nameof(ServerInfo.PoolName) || args.PropertyName == nameof(ServerInfo.SiteName))
                        {
                            OnPropertyChanged(nameof(CanUseIisBulkOperations));
                        }
                    });
                    _serverHandlers[item] = handler;
                    item.PropertyChanged += handler;
                }
            }

            if (e.OldItems != null)
            {
                foreach (ServerInfo item in e.OldItems)
                {
                    if (_serverHandlers.TryGetValue(item, out var handler))
                    {
                        item.PropertyChanged -= handler;
                        _serverHandlers.Remove(item);
                    }
                }
            }

            if (!_isInitializing)
            {
                SaveServerSettings();
                // Notify when ServerList changes (items added/removed)
                OnPropertyChanged(nameof(CanUseSyncthingBulkOperations));
                OnPropertyChanged(nameof(CanUseIisBulkOperations));
            }
        };

        _isInitializing = false;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var settings = await _mediator.Send(new LoadSettingsQuery()).ConfigureAwait(false);
            
            if (OperatingSystem.IsWindows())
            {
                LoadIisSites(settings);
            }
            else
            {
                ApplyServerSettings(settings);
                IsFolderMode = true;
                OnPropertyChanged(nameof(IsFolderMode));
                OnPropertyChanged(nameof(IsIisMode));
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to initialize settings: {ex.Message}");
        }
        finally
        {
            _isInitializing = false;

            // Notify UI about Syncthing bulk operations availability after initialization
            OnPropertyChanged(nameof(CanUseSyncthingBulkOperations));

            // Note: InitializeSyncthingEventsListener() is called in ApplyServerSettings after settings are loaded
        }
    }
    
    public Version SitePathWithVersion
    {
        get => _sitePathWithVersion;
        set => SetProperty(ref _sitePathWithVersion, value);
    }

    public bool IsWindows => OperatingSystem.IsWindows();
    public string ServerPanelButtonText => IsServerPanelVisible ? "Servers sync ◂" : "Servers sync ▸";

    [ObservableProperty]
    private bool _isFolderMode;

    public bool IsIisMode => !IsFolderMode;

    [ObservableProperty]
    private IisSiteInfo? _selectedIisSite;

    [ObservableProperty]
    private string? _sitePath;

    [ObservableProperty]
    private string? _serviceName;

    [ObservableProperty]
    private string? _packagesPath;

    [ObservableProperty]
    private bool _prevalidateBeforeInstall;

    [ObservableProperty]
    private bool _resetUnlockedPackageFlags;

    [ObservableProperty]
    private string? _packagesToDeleteBefore;

    [ObservableProperty]
    private string? _packagesToDeleteAfter;

    [ObservableProperty]
    private string _newServerName = string.Empty;

    [ObservableProperty]
    private bool _isServerPanelVisible;
    
    [ObservableProperty]
    private bool _isAutoScrollEnabled = true;
    
    [ObservableProperty]
    private bool _isWrapTextEnabled;
    
    [ObservableProperty]
    private bool _isLogToFileEnabled;

    [ObservableProperty]
    private bool _skipRedisClear;

    [ObservableProperty]
    private bool _skipServerRestart;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _isFileDesignModeEnabled;


    [ObservableProperty]
    private bool _isServerControlsEnabled = true;

    [ObservableProperty]
    private bool _isLoadingServerStatuses = false;

    partial void OnIsLoadingServerStatusesChanged(bool value)
    {
        OnPropertyChanged(nameof(AreControlsEnabled));
    }

    [ObservableProperty]
    private bool _enableFileCopySynchronization = true;

    [ObservableProperty]
    private bool _useSyncthingForSync = false;

    [ObservableProperty]
    private string? _syncthingApiUrl;

    [ObservableProperty]
    private string? _syncthingApiKey;

    [ObservableProperty]
    private string _redisServiceStatus = "";

    [ObservableProperty]
    private string? _redisServiceName;

    public bool IsBusy => _operationsService.IsBusy;

    /// <summary>
    /// Returns true when UI controls should be enabled (not busy and not loading statuses)
    /// </summary>
    private bool _isValidating;
    public bool AreControlsEnabled => !IsBusy && !IsLoadingServerStatuses && !_isValidating;

    public string SyncModeButtonText
    {
        get
        {
            if (UseSyncthingForSync)
                return "SyncSystem: Syncthing";
            if (EnableFileCopySynchronization)
                return "SyncSystem: File Copy";
            return "SyncSystem: Built-in";
        }
    }

    public bool HasSyncthingApiUrl => !string.IsNullOrEmpty(SyncthingApiUrl);

    public string StartButtonText => _operationsService.StartButtonText;

    public bool IsStopButtonEnabled => _operationsService.IsStopButtonEnabled;

    /// <summary>
    /// True when package paths are empty — user can choose between Compile (default) and Compile All (dropdown).
    /// When any package path is set, full rebuild is forced and the dropdown is hidden.
    /// </summary>
    public bool IsCompileChoiceAvailable =>
        string.IsNullOrWhiteSpace(PackagesPath) &&
        string.IsNullOrWhiteSpace(PackagesToDeleteBefore) &&
        string.IsNullOrWhiteSpace(PackagesToDeleteAfter);

    public bool IsFullRebuildOnly => !IsCompileChoiceAvailable;

    public bool HasIisSites => IisSites.Any(site => !string.IsNullOrEmpty(site.Path) && !string.IsNullOrEmpty(site.PoolName));

    public bool CanRestartLocalIis =>
        IsWindows &&
        IsIisMode &&
        SelectedIisSite != null &&
        !string.IsNullOrWhiteSpace(SelectedIisSite.PoolName) &&
        !IsBusy;


    public ObservableCollection<IisSiteInfo> IisSites { get; } = new();
    public ObservableCollection<ServerInfo> ServerList { get; } = new();

    [RelayCommand]
    private void AddServer()
    {
        if (!string.IsNullOrWhiteSpace(NewServerName) && ServerList.All(s => s.Name?.Equals(NewServerName.Trim(), StringComparison.OrdinalIgnoreCase) != true))
        {
            ServerList.Add(new ServerInfo
            {
                Name = new ServerName(NewServerName.Trim()),
                NetworkPath = new NetworkPath(NewServerName.Trim())
            });

            NewServerName = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveServer(ServerInfo server)
    {
        if (ServerList.Contains(server))
            ServerList.Remove(server);
    }
    
    [RelayCommand]
    private void ToggleServerPanel()
    {
        IsServerPanelVisible = !IsServerPanelVisible;
    }

    public void ClearLogCommand()
    {
        _output.Clear();
    }

    private void ClearCharts()
    {
        _metricsService.ClearHistory();
        foreach (var key in _pieSeriesMap.Keys.ToList())
        {
            _pieSeriesCollection.Remove(_pieSeriesMap[key].Series);
            _pieSeriesMap.Remove(key);
        }
        foreach (var key in _pipelineSeriesMap.Keys.ToList())
        {
            _pipelineSeriesCollection.Remove(_pipelineSeriesMap[key].Series);
            _pipelineSeriesMap.Remove(key);
        }
        PipelineXAxes = new[] { new Axis() };
        PipelineYAxes = new[] { new Axis() };
        TotalDurationText = string.Empty;
    }
    
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshServerStatus(ServerInfo server)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await _statusService.RefreshServerStatusOnUIThreadAsync(server);
    }

    [RelayCommand]
    private async Task RefreshAllServersStatus()
    {
        if (ServerList.Count == 0) return;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            IsLoadingServerStatuses = true;
            await _statusService.RefreshMultipleServerStatusOnUIThreadAsync(ServerList.ToArray());
        }
        finally
        {
            IsLoadingServerStatuses = false;
        }
    }
    
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StopPool(ServerInfo server)
    {
        if (string.IsNullOrWhiteSpace(server.PoolName))
        {
            _output.WriteLine($"[ERROR] Pool name is not configured for server '{server.Name ?? "Unknown"}'");
            return;
        }

        server.IsStatusLoading = true;
        
        try
        {
            _output.WriteLine($"[INFO] Stopping application pool '{server.PoolName}' on server '{server.Name ?? "Unknown"}'...");
            var result = await _iisManager.StopAppPoolAsync(server.Name ?? Environment.MachineName, server.PoolName, CancellationToken.None);
            if (result.IsSuccess)
            {
                if (OperatingSystem.IsWindows())
                {
                    var serverInfo = await _statusService.RefreshServerStatusOnUIThreadAsync(server);
                    _output.WriteLine($"[SUCCESS] Application pool '{server.PoolName}' status {serverInfo.PoolStatus} on server '{server.Name ?? "Unknown"}'.");
                }
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to stop application pool: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to stop application pool '{server.PoolName}': {ex.Message}");
        }
        finally
        {
            server.IsStatusLoading = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartPool(ServerInfo server)
    {
        if (string.IsNullOrWhiteSpace(server.PoolName))
        {
            _output.WriteLine($"[ERROR] Pool name is not configured for server '{server.Name ?? "Unknown"}'");
            return;
        }

        server.IsStatusLoading = true;
        
        try
        {
            _output.WriteLine($"[INFO] Starting application pool '{server.PoolName}' on server '{server.Name ?? "Unknown"}'...");
            
            var result = await _iisManager.StartAppPoolAsync(server.Name ?? Environment.MachineName, server.PoolName, CancellationToken.None);
            if (result.IsSuccess)
            {
                if (OperatingSystem.IsWindows())
                {
                    var serverInfo = await _statusService.RefreshServerStatusOnUIThreadAsync(server);
                    _output.WriteLine($"[SUCCESS] Application pool '{server.SiteName}' status {serverInfo.SiteStatus} on server '{server.Name ?? "Unknown"}'.");
                }
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to start application pool: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to start application pool '{server.PoolName}': {ex.Message}");
        }
        finally
        {
            server.IsStatusLoading = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StopSite(ServerInfo server)
    {
        if (string.IsNullOrWhiteSpace(server.SiteName))
        {
            _output.WriteLine($"[ERROR] Site name is not configured for server '{server.Name ?? "Unknown"}'");
            return;
        }

        server.IsStatusLoading = true;
        
        try
        {
            _output.WriteLine($"[INFO] Stopping website '{server.SiteName}' on server '{server.Name ?? "Unknown"}'...");
            
            var result = await _iisManager.StopWebsiteAsync(server.Name ?? Environment.MachineName, server.SiteName, CancellationToken.None);
            if (result.IsSuccess)
            {
                if (OperatingSystem.IsWindows())
                {
                    var serverInfo = await _statusService.RefreshServerStatusOnUIThreadAsync(server);
                    _output.WriteLine($"[SUCCESS] Website '{server.SiteName}' status {serverInfo.SiteStatus} on server '{server.Name ?? "Unknown"}'.");
                }
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to stop website: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to stop website '{server.SiteName}': {ex.Message}");
        }
        finally
        {
            server.IsStatusLoading = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartSite(ServerInfo server)
    {
        if (string.IsNullOrWhiteSpace(server.SiteName))
        {
            _output.WriteLine($"[ERROR] Site name is not configured for server '{server.Name ?? "Unknown"}'");
            return;
        }

        server.IsStatusLoading = true;

        try
        {
            _output.WriteLine($"[INFO] Starting website '{server.SiteName}' on server '{server.Name ?? "Unknown"}'...");

            var result = await _iisManager.StartWebsiteAsync(server.Name ?? Environment.MachineName, server.SiteName, CancellationToken.None);
            if (result.IsSuccess)
            {
                if (OperatingSystem.IsWindows())
                {
                    var serverInfo = await _statusService.RefreshServerStatusOnUIThreadAsync(server);
                    _output.WriteLine($"[SUCCESS] Website '{server.SiteName}' status {serverInfo.SiteStatus} on server '{server.Name ?? "Unknown"}'.");
                }
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to start website: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to start website '{server.SiteName}': {ex.Message}");
        }
        finally
        {
            server.IsStatusLoading = false;
        }
    }
    
    [RelayCommand]
    private async Task Start()
    {
        await _operationsService.StartOperation(this, fullRebuild: false);
    }

    [RelayCommand]
    private async Task StartFull()
    {
        await _operationsService.StartOperation(this, fullRebuild: true);
    }

    [RelayCommand]
    private void Stop()
    {
        _operationsService.StopOperation();
    }

    [RelayCommand]
    private async Task RestartLocalIis()
    {
        if (SelectedIisSite == null || string.IsNullOrWhiteSpace(SelectedIisSite.PoolName))
        {
            _output.WriteLine("[INFO] No IIS site selected — cannot restart pool/site.");
            return;
        }

        _output.Clear();
        var local = new ServerInfo
        {
            Name = new ServerName(Environment.MachineName),
            SiteName = SelectedIisSite.IsVirtualApp ? string.Empty : (SelectedIisSite.Name ?? string.Empty),
            PoolName = SelectedIisSite.PoolName ?? string.Empty
        };
        await _operationsService.RestartAllIisAsync(new[] { local });
    }

    [RelayCommand]
    private async Task BrowseSitePath()
    {
        var path = await _dialogService.OpenFolderPickerAsync("Select Site Path");
        if (path != null)
        {
            SitePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowsePackagesPath()
    {
        var path = await _dialogService.OpenFolderPickerAsync("Select Packages Path");
        if (path != null)
        {
            PackagesPath = path;
        }
    }

    [RelayCommand]
    private async Task LoadLicense()
    {
        var sitePath = GetResolvedSitePath();
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            _output.WriteLine("[ERROR] Site path is not configured.");
            return;
        }

        var licFilePath = await _dialogService.OpenFilePickerAsync("Select license file", new[] { "*.tls" });
        if (string.IsNullOrWhiteSpace(licFilePath)) return;

        await _operationsService.ExecuteWscOperationAsync(sitePath, "LoadLicResponse",
            () => _workspacePreparer.LoadLicResponse(sitePath, licFilePath));
    }

    public async Task ExecuteSaveLicenseRequest(string customerId, string filePath)
    {
        var sitePath = GetResolvedSitePath();
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            _output.WriteLine("[ERROR] Site path is not configured.");
            return;
        }

        var destinationPath = Path.GetDirectoryName(filePath) ?? "";
        var fileName = Path.GetFileName(filePath);

        await _operationsService.ExecuteWscOperationAsync(sitePath, "SaveLicenseRequest",
            () => _workspacePreparer.SaveLicenseRequest(sitePath, destinationPath, customerId, fileName));
    }

    [RelayCommand]
    private async Task DownloadPackagesOp()
    {
        var sitePath = GetResolvedSitePath();
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            _output.WriteLine("[ERROR] Site path is not configured.");
            return;
        }

        await _metricsService.MeasureAsync("creatio_to_fs", async () =>
        {
            await _operationsService.ExecuteWscOperationAsync(sitePath, "LoadPackagesToFileSystem",
                () => _workspacePreparer.DownloadPackages(sitePath, string.Empty));
            return true;
        });
    }

    [RelayCommand]
    private async Task ValidatePackagesOp()
    {
        if (_operationsService.IsBusy)
        {
            _output.WriteLine("[WARN] Another operation is already running.");
            return;
        }

        var sitePath = GetResolvedSitePath();
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            _output.WriteLine("[ERROR] Site path is not configured.");
            return;
        }

        var pkgPath = _workspacePreparer.GetPkgPath(sitePath);
        if (string.IsNullOrWhiteSpace(pkgPath))
        {
            _output.WriteLine("[ERROR] Packages path is not configured.");
            return;
        }

        _output.Clear();
        _output.WriteLine("Running package cleaning & validation...");
        _isValidating = true;
        OnPropertyChanged(nameof(AreControlsEnabled));

        try
        {
            await _metricsService.MeasureAsync("clean_validate", async () =>
            {
                await Task.Run(() =>
                {
                    var cleanResult = _packageCleaner.CleanPackages(pkgPath);
                    if (!cleanResult.HasInvalidOtherJson && !cleanResult.HasInvalidJson && !cleanResult.HasCircularDependencies)
                    {
                        _output.WriteLine("No issues found.");
                    }
                });
                return true;
            });
        }
        finally
        {
            _isValidating = false;
            OnPropertyChanged(nameof(AreControlsEnabled));
        }
    }

    [RelayCommand]
    private async Task LoadPackagesToDBOp()
    {
        var sitePath = GetResolvedSitePath();
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            _output.WriteLine("[ERROR] Site path is not configured.");
            return;
        }

        await _metricsService.MeasureAsync("fs_to_creatio", async () =>
        {
            await _operationsService.ExecuteWscOperationAsync(sitePath, "LoadPackagesToDB",
                () => _workspacePreparer.LoadPackagesToDb(sitePath));
            return true;
        });
    }

    private string? GetResolvedSitePath()
    {
        if (IsIisMode)
            return SelectedIisSite?.Path;
        return SitePath;
    }

    private void SetControlsEnabled(bool isEnabled)
    {
        IsServerControlsEnabled = isEnabled;
    }

    partial void OnIsFolderModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIisMode));
        OnPropertyChanged(nameof(CanUseIisBulkOperations));
        OnPropertyChanged(nameof(CanRestartLocalIis));
        SaveServerSettings();
    }

    partial void OnIsServerPanelVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ServerPanelButtonText));
        SaveServerSettings();
    }

    partial void OnPackagesPathChanged(string? value)
    {
        SaveServerSettings();
        OnPropertyChanged(nameof(IsCompileChoiceAvailable));
        OnPropertyChanged(nameof(IsFullRebuildOnly));
    }
    partial void OnPrevalidateBeforeInstallChanged(bool value) => SaveServerSettings();
    partial void OnResetUnlockedPackageFlagsChanged(bool value) => SaveServerSettings();
    partial void OnSkipRedisClearChanged(bool value) => SaveServerSettings();
    partial void OnSkipServerRestartChanged(bool value) => SaveServerSettings();
    partial void OnPackagesToDeleteBeforeChanged(string? value)
    {
        SaveServerSettings();
        OnPropertyChanged(nameof(IsCompileChoiceAvailable));
        OnPropertyChanged(nameof(IsFullRebuildOnly));
    }
    partial void OnPackagesToDeleteAfterChanged(string? value)
    {
        SaveServerSettings();
        OnPropertyChanged(nameof(IsCompileChoiceAvailable));
        OnPropertyChanged(nameof(IsFullRebuildOnly));
    }
    partial void OnSitePathChanged(string? value)
    {
        SaveServerSettings();
        RefreshFileDesignMode();
    }
    partial void OnServiceNameChanged(string? value) => SaveServerSettings();
    partial void OnSelectedIisSiteChanged(IisSiteInfo? value)
    {
        OnPropertyChanged(nameof(SelectedIisSiteVersion));
        OnPropertyChanged(nameof(CanRestartLocalIis));
        SaveServerSettings();
        RefreshFileDesignMode();
    }

    private void RefreshFileDesignMode()
    {
        var sitePath = GetResolvedSitePath();
        IsFileDesignModeEnabled = !string.IsNullOrWhiteSpace(sitePath) && _workspacePreparer.IsFileDesignModeEnabled(sitePath);
    }

    public Version? SelectedIisSiteVersion => SelectedIisSite?.Version;

    partial void OnEnableFileCopySynchronizationChanged(bool value)
    {
        SaveServerSettings();
        OnPropertyChanged(nameof(SyncModeButtonText));
        OnPropertyChanged(nameof(CanUseSyncthingBulkOperations));
    }

    partial void OnUseSyncthingForSyncChanged(bool value)
    {
        SaveServerSettings();
        OnPropertyChanged(nameof(SyncModeButtonText));
        OnPropertyChanged(nameof(CanUseSyncthingBulkOperations));

        // Reinitialize events listener when Syncthing is enabled/disabled (only after initial settings load)
        if (_settingsLoaded)
        {
            InitializeSyncthingEventsListener();
        }
    }

    partial void OnSyncthingApiUrlChanged(string? value)
    {
        SaveServerSettings();
        OnPropertyChanged(nameof(HasSyncthingApiUrl));
        OnPropertyChanged(nameof(CanUseSyncthingBulkOperations));

        // Reinitialize events listener when API URL changes (only after initial settings load)
        if (_settingsLoaded)
        {
            InitializeSyncthingEventsListener();
        }
    }

    partial void OnSyncthingApiKeyChanged(string? value)
    {
        SaveServerSettings();
        OnPropertyChanged(nameof(CanUseSyncthingBulkOperations));

        // Reinitialize events listener when API Key changes (only after initial settings load)
        if (_settingsLoaded)
        {
            InitializeSyncthingEventsListener();
        }
    }

    private void LoadIisSites(AppSettings? settings)
    {
        _iisService.LoadIisSites(IisSites, success =>
        {
            OnPropertyChanged(nameof(HasIisSites));
            if (success && settings != null)
            {
                ApplyServerSettings(settings);
            }
        });
    }

    private void ApplyServerSettings(AppSettings settings)
    {
        SitePath = settings.SitePath;
        ServiceName = settings.ServiceName;
        PackagesPath = settings.PackagesPath;
        PrevalidateBeforeInstall = settings.PrevalidateBeforeInstall;
        ResetUnlockedPackageFlags = settings.ResetUnlockedPackageFlags;
        SkipRedisClear = settings.SkipRedisClear;
        SkipServerRestart = settings.SkipServerRestart;
        PackagesToDeleteBefore = settings.PackagesToDeleteBefore;
        PackagesToDeleteAfter = settings.PackagesToDeleteAfter;

        if (!string.IsNullOrWhiteSpace(settings.SelectedIisSiteName))
        {
            var match = IisSites.FirstOrDefault(x => x.Name == settings.SelectedIisSiteName);
            SelectedIisSite = match ?? (IisSites.Count > 0 ? IisSites[0] : null);
        }
        else if (IisSites.Count > 0)
        {
            SelectedIisSite = IisSites[0];
        }

        IsFolderMode = !settings.IsIisMode;
        IsServerPanelVisible = settings.IsServerPanelVisible;
        EnableFileCopySynchronization = settings.EnableFileCopySynchronization;
        UseSyncthingForSync = settings.UseSyncthingForSync;
        SyncthingApiUrl = settings.SyncthingApiUrl;
        SyncthingApiKey = settings.SyncthingApiKey;

        ServerList.Clear();
        foreach (var server in settings.ServerList)
            ServerList.Add(server);

        // Notify UI about Syncthing bulk operations availability after settings are applied
        if (!_isInitializing)
        {
            OnPropertyChanged(nameof(CanUseSyncthingBulkOperations));
        }

        // On Windows, settings are applied asynchronously via LoadIisSites callback
        // Mark settings as loaded and initialize listener only once after first load
        if (!_settingsLoaded)
        {
            _settingsLoaded = true;
            InitializeSyncthingEventsListener();

            // Refresh server statuses at startup if panel is visible
            if (IsServerPanelVisible && ServerList.Any() && OperatingSystem.IsWindows())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500); // Small delay to let UI finish loading

                        // Block buttons during initial status refresh
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            IsLoadingServerStatuses = true;
                        });

                        await _statusService.RefreshMultipleServerStatusOnUIThreadAsync(ServerList.ToArray(), CancellationToken.None);

                        // Unblock buttons after refresh
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            IsLoadingServerStatuses = false;
                        });
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"[WARNING] Failed to refresh server statuses at startup: {ex.Message}");

                        // Ensure buttons are unblocked even on error
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            IsLoadingServerStatuses = false;
                        });
                    }
                });
            }
        }
    }

    private void SaveServerSettings()
    {
        if (_isInitializing) return;

        var settings = new AppSettings
        {
            SitePath = IsFolderMode ? SitePath : null,
            ServiceName = IsFolderMode ? ServiceName : null,
            SelectedIisSiteName = IsIisMode ? SelectedIisSite?.Name : null,
            PackagesPath = PackagesPath,
            PrevalidateBeforeInstall = PrevalidateBeforeInstall,
            ResetUnlockedPackageFlags = ResetUnlockedPackageFlags,
            SkipRedisClear = SkipRedisClear,
            SkipServerRestart = SkipServerRestart,
            PackagesToDeleteBefore = PackagesToDeleteBefore,
            PackagesToDeleteAfter = PackagesToDeleteAfter,
            ServerList = new ObservableCollection<ServerInfo>(ServerList.Select(s => new ServerInfo
            {
                Name = s.Name,
                NetworkPath = s.NetworkPath,
                PoolName = s.PoolName,
                SiteName = s.SiteName,
                SyncthingFolderIds = new List<string>(s.SyncthingFolderIds),
                SyncthingDeviceId = s.SyncthingDeviceId
            })),
            IsIisMode = IsIisMode,
            IsServerPanelVisible = IsServerPanelVisible,
            EnableFileCopySynchronization = EnableFileCopySynchronization,
            UseSyncthingForSync = UseSyncthingForSync,
            SyncthingApiUrl = SyncthingApiUrl,
            SyncthingApiKey = SyncthingApiKey
        };

        _mediator.Send(new SaveSettingsCommand(settings));
    }

    [RelayCommand]
    private async Task StartService(ServerInfo server)
    {
        if (string.IsNullOrWhiteSpace(server.ServiceName))
        {
            _output.WriteLine($"[ERROR] Service name is not configured for server '{server.Name ?? "Unknown"}'");
            return;
        }

        var result = await _iisManager.StartServiceAsync(server.Name ?? Environment.MachineName, server.ServiceName, CancellationToken.None);
        if (result.IsSuccess)
        {
            await _statusService.RefreshServerStatusOnUIThreadAsync(server);
        }
        else
        {
            _output.WriteLine($"[ERROR] Failed to start service: {result.ErrorMessage}");
        }
    }

    [RelayCommand]
    private async Task StopService(ServerInfo server)
    {
        if (string.IsNullOrWhiteSpace(server.ServiceName))
        {
            _output.WriteLine($"[ERROR] Service name is not configured for server '{server.Name ?? "Unknown"}'");
            return;
        }

        var result = await _iisManager.StopServiceAsync(server.Name ?? Environment.MachineName, server.ServiceName, CancellationToken.None);
        if (result.IsSuccess)
        {
            await _statusService.RefreshServerStatusOnUIThreadAsync(server);
        }
        else
        {
            _output.WriteLine($"[ERROR] Failed to stop service: {result.ErrorMessage}");
        }
    }

    [RelayCommand]
    private async Task StartMainService()
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            _output.WriteLine("[ERROR] Service name is not specified. Please enter a service name.");
            return;
        }

        try
        {
            var result = await _systemServiceManager.StartServiceAsync(ServiceName);
            
            if (result)
            {
                _output.WriteLine($"[SUCCESS] Service '{ServiceName}' started successfully.");
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to start service '{ServiceName}'.");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to start service '{ServiceName}': {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopMainService()
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            _output.WriteLine("[ERROR] Service name is not specified. Please enter a service name.");
            return;
        }

        try
        {
            var result = await _systemServiceManager.StopServiceAsync(ServiceName);

            if (result)
            {
                _output.WriteLine($"[SUCCESS] Service '{ServiceName}' stopped successfully.");
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to stop service '{ServiceName}'.");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to stop service '{ServiceName}': {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearRedis()
    {
        var manager = CreateRedisManager();
        if (manager == null)
        {
            _output.WriteLine("[ERROR] Could not initialize Redis manager.");
            return;
        }
        manager.Clear();
    }

    [RelayCommand]
    private void CheckRedisStatus()
    {
        var manager = CreateRedisManager();
        if (manager == null)
        {
            _output.WriteLine("[ERROR] Could not initialize Redis manager.");
            return;
        }
        manager.CheckStatus();
    }

    private IRedisManager? CreateRedisManager()
    {
        string? path = null;
        if (IsFolderMode && !string.IsNullOrWhiteSpace(SitePath))
            path = SitePath;
        else if (IsIisMode && !string.IsNullOrWhiteSpace(SelectedIisSite?.Path))
            path = SelectedIisSite!.Path;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;

        try
        {
            return _redisManagerFactory.Create(path);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to create Redis manager: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Property to check if bulk Syncthing operations are available
    /// </summary>
    public bool CanUseSyncthingBulkOperations =>
        UseSyncthingForSync &&
        !string.IsNullOrEmpty(SyncthingApiUrl) &&
        !string.IsNullOrEmpty(SyncthingApiKey) &&
        ServerList.Any(s => s.SyncthingFolderIds.Count > 0);

    public IAsyncRelayCommand ResumeAllSyncthingFoldersCommand { get; }
    public IAsyncRelayCommand PauseAllSyncthingFoldersCommand { get; }

    private async Task ResumeAllSyncthingFolders()
    {
        if (!UseSyncthingForSync)
        {
            _output.WriteLine("[INFO] Syncthing sync is not enabled");
            return;
        }

        var serversWithSyncthing = ServerList
            .Where(s => s.SyncthingFolderIds.Count > 0)
            .ToList();

        if (!serversWithSyncthing.Any())
        {
            _output.WriteLine("[INFO] No servers have Syncthing folders configured");
            return;
        }

        // Count total folders across all servers
        var totalFolders = serversWithSyncthing.Sum(s => s.SyncthingFolderIds.Count);
        _output.WriteLine($"[INFO] ▶️ Resuming synchronization for {totalFolders} Syncthing folders across {serversWithSyncthing.Count} servers...");

        var monitor = _operationsService.GetSyncthingMonitor();
        if (monitor == null)
        {
            _output.WriteLine("[ERROR] Syncthing monitor is not configured");
            return;
        }

        var successCount = 0;
        foreach (var server in serversWithSyncthing)
        {
            foreach (var folderId in server.SyncthingFolderIds)
            {
                var result = await monitor.ResumeFolderAsync(folderId, CancellationToken.None);
                if (result)
                {
                    successCount++;
                }
            }
        }

        _output.WriteLine($"[OK] ✅ Resumed {successCount}/{totalFolders} folders");

        // Refresh status for all servers
        await Task.Delay(1000); // Give Syncthing a moment to update
        await _statusService.RefreshMultipleServerStatusOnUIThreadAsync(serversWithSyncthing.ToArray(), CancellationToken.None);
    }

    private async Task PauseAllSyncthingFolders()
    {
        if (!UseSyncthingForSync)
        {
            _output.WriteLine("[INFO] Syncthing sync is not enabled");
            return;
        }

        var serversWithSyncthing = ServerList
            .Where(s => s.SyncthingFolderIds.Count > 0)
            .ToList();

        if (!serversWithSyncthing.Any())
        {
            _output.WriteLine("[INFO] No servers have Syncthing folders configured");
            return;
        }

        // Count total folders across all servers
        var totalFolders = serversWithSyncthing.Sum(s => s.SyncthingFolderIds.Count);
        _output.WriteLine($"[INFO] ⏸️ Pausing synchronization for {totalFolders} Syncthing folders across {serversWithSyncthing.Count} servers...");

        var monitor = _operationsService.GetSyncthingMonitor();
        if (monitor == null)
        {
            _output.WriteLine("[ERROR] Syncthing monitor is not configured");
            return;
        }

        var successCount = 0;
        foreach (var server in serversWithSyncthing)
        {
            foreach (var folderId in server.SyncthingFolderIds)
            {
                var result = await monitor.PauseFolderAsync(folderId, CancellationToken.None);
                if (result)
                {
                    successCount++;
                }
            }
        }

        _output.WriteLine($"[OK] ⏸️ Paused {successCount}/{serversWithSyncthing.Count} folders");

        // Refresh status for all servers
        await Task.Delay(1000); // Give Syncthing a moment to update
        await _statusService.RefreshMultipleServerStatusOnUIThreadAsync(serversWithSyncthing.ToArray(), CancellationToken.None);
    }

    #region IIS Bulk Operations

    /// <summary>
    /// Command properties for IIS bulk operations
    /// </summary>
    public IAsyncRelayCommand StartAllIisCommand { get; }
    public IAsyncRelayCommand StopAllIisCommand { get; }

    /// <summary>
    /// Property to control visibility of IIS bulk operation buttons
    /// Only visible on Windows when IIS mode is enabled and there are servers with IIS configured
    /// </summary>
    public bool CanUseIisBulkOperations =>
        IsWindows &&
        IsIisMode &&
        ServerList.Any(s => !string.IsNullOrEmpty(s.PoolName) || !string.IsNullOrEmpty(s.SiteName));

    /// <summary>
    /// Start all IIS sites and application pools for servers in the list
    /// </summary>
    private async Task StartAllIis()
    {
        if (!IsWindows)
        {
            _output.WriteLine("[INFO] IIS is only available on Windows");
            return;
        }

        if (!IsIisMode)
        {
            _output.WriteLine("[INFO] IIS mode is not enabled");
            return;
        }

        var serversWithIis = ServerList
            .Where(s => !string.IsNullOrEmpty(s.PoolName) || !string.IsNullOrEmpty(s.SiteName))
            .ToList();

        if (!serversWithIis.Any())
        {
            _output.WriteLine("[INFO] No servers have IIS sites or pools configured");
            return;
        }

        await _operationsService.StartAllIisAsync(serversWithIis);
        // UI is automatically updated inside StartAllIisAsync for each server
    }

    /// <summary>
    /// Stop all IIS sites and application pools for servers in the list
    /// </summary>
    private async Task StopAllIis()
    {
        if (!IsWindows)
        {
            _output.WriteLine("[INFO] IIS is only available on Windows");
            return;
        }

        if (!IsIisMode)
        {
            _output.WriteLine("[INFO] IIS mode is not enabled");
            return;
        }

        var serversWithIis = ServerList
            .Where(s => !string.IsNullOrEmpty(s.PoolName) || !string.IsNullOrEmpty(s.SiteName))
            .ToList();

        if (!serversWithIis.Any())
        {
            _output.WriteLine("[INFO] No servers have IIS sites or pools configured");
            return;
        }

        await _operationsService.StopAllIisAsync(serversWithIis);
        // UI is automatically updated inside StopAllIisAsync for each server
    }

    #endregion

    #region Syncthing Events Listener

    private SyncthingEventsListener? _syncthingEventsListener;

    /// <summary>
    /// Initialize and start Syncthing Events Listener for real-time synchronization monitoring
    /// </summary>
    private void InitializeSyncthingEventsListener()
    {
        // Stop existing listener if any
        if (_syncthingEventsListener != null)
        {
            _ = _syncthingEventsListener.StopAsync();
            _syncthingEventsListener.Dispose();
            _syncthingEventsListener = null;
        }
        
        if (!UseSyncthingForSync)
        {
            return;
        }

        // Only start if Syncthing is enabled and configured
        if (string.IsNullOrEmpty(SyncthingApiUrl) ||
            string.IsNullOrEmpty(SyncthingApiKey))
        {
            _output.WriteLine("[INFO] Syncthing Events Listener not started (not configured)");
            return;
        }

        try
        {
            var httpClientFactory = App.Services?.GetService(typeof(System.Net.Http.IHttpClientFactory))
                as System.Net.Http.IHttpClientFactory;

            if (httpClientFactory == null)
            {
                _output.WriteLine("[ERROR] HttpClientFactory not available");
                return;
            }

            _syncthingEventsListener = new SyncthingEventsListener(
                httpClientFactory,
                _output,
                SyncthingApiUrl,
                SyncthingApiKey
            );

            // Subscribe to StateChanged events
            _syncthingEventsListener.OnStateChanged += async (data) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateServerStateFromEvent(data);
                });
            };

            // Subscribe to FolderCompletion events
            _syncthingEventsListener.OnFolderCompletion += async (data) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateServerCompletionFromEvent(data);
                });
            };

            // Subscribe to ItemFinished events
            _syncthingEventsListener.OnItemFinished += async (data) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateServerLastSyncedFile(data);
                });
            };

            // Subscribe to DeviceConnected/Disconnected events
            _syncthingEventsListener.OnDeviceConnected += async (data) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateServerDeviceStatus(data.Id, connected: true);
                });
            };

            _syncthingEventsListener.OnDeviceDisconnected += async (data) =>
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateServerDeviceStatus(data.Id, connected: false);
                });
            };

            // Start listening
            _syncthingEventsListener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500); // Small delay to let listener initialize

                    var syncthingServers = ServerList
                        .Where(s => !string.IsNullOrEmpty(s.SyncthingDeviceId) &&
                                   s.SyncthingFolderIds.Count > 0)
                        .ToArray();

                    if (syncthingServers.Any())
                    {
                        // Block buttons during initial status refresh
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            IsLoadingServerStatuses = true;
                        });

                        await _statusService.RefreshMultipleServerStatusOnUIThreadAsync(syncthingServers, CancellationToken.None);

                        // Unblock buttons after refresh
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            IsLoadingServerStatuses = false;
                        });
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"[ERROR] Failed to perform initial status refresh: {ex.Message}");

                    // Ensure buttons are unblocked even on error
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsLoadingServerStatuses = false;
                    });
                }
            });
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to initialize Syncthing Events Listener: {ex.Message}");
        }
    }

    /// <summary>
    /// Update server state from StateChanged event
    /// </summary>
    private void UpdateServerStateFromEvent(StateChangedEventData data)
    {
        // Find server that contains this folder ID
        var server = ServerList.FirstOrDefault(s => s.SyncthingFolderIds.Contains(data.Folder));
        if (server == null)
            return;

        // Update folder-specific state
        server.UpdateFolderSyncState(
            data.Folder,
            server.SyncthingCompletionPercent, // Keep existing completion
            server.SyncthingNeedBytes, // Keep existing bytes
            server.SyncthingNeedItems, // Keep existing items
            data.To); // Update state

        // Aggregated state is automatically recalculated in UpdateFolderSyncState
        // Update status text based on aggregated state
        server.SyncthingStatus = server.SyncthingCurrentState switch
        {
            "idle" => server.SyncthingCompletionPercent >= 100 ? "✅ Up to Date" : "⏸️ Idle",
            "scanning" => "🔍 Scanning",
            "syncing" => $"🔄 Syncing ({server.SyncthingCompletionPercent:F1}%)",
            "error" => "❌ Error",
            _ => server.SyncthingCurrentState
        };
    }

    /// <summary>
    /// Update server completion from FolderCompletion event
    /// </summary>
    private void UpdateServerCompletionFromEvent(FolderCompletionEventData data)
    {
        // Find server that contains this folder ID and has matching device ID
        var server = ServerList.FirstOrDefault(s =>
            s.SyncthingFolderIds.Contains(data.Folder) &&
            s.SyncthingDeviceId == data.Device);

        if (server == null)
            return;

        // Update folder-specific state with new completion data
        server.UpdateFolderSyncState(
            data.Folder,
            data.Completion,
            data.NeedBytes,
            data.NeedItems,
            server.SyncthingCurrentState); // Keep existing state

        // Aggregated values are automatically recalculated in UpdateFolderSyncState
        // Update status text based on aggregated values
        if (server.SyncthingNeedBytes > 0 || server.SyncthingNeedItems > 0)
        {
            server.SyncthingStatus = $"🔄 Syncing ({server.SyncthingCompletionPercent:F1}%)";
        }
        else
        {
            server.SyncthingStatus = "✅ Up to Date";
        }

        // Notify UI about changes to formatted property
        server.OnPropertyChanged(nameof(ServerInfo.SyncthingRemainingFormatted));
    }

    /// <summary>
    /// Update last synced file from ItemFinished event
    /// </summary>
    private void UpdateServerLastSyncedFile(ItemFinishedEventData data)
    {
        // Find server that contains this folder ID
        var server = ServerList.FirstOrDefault(s => s.SyncthingFolderIds.Contains(data.Folder));
        if (server == null)
            return;

        // Only show successful syncs (no error)
        if (string.IsNullOrEmpty(data.Error))
        {
            // Extract filename from path
            var fileName = System.IO.Path.GetFileName(data.Item);

            // Update folder-specific state with last synced file
            server.UpdateFolderSyncState(
                data.Folder,
                server.SyncthingCompletionPercent, // Keep existing values
                server.SyncthingNeedBytes,
                server.SyncthingNeedItems,
                server.SyncthingCurrentState,
                fileName); // Update last synced file
        }
    }

    /// <summary>
    /// Update server device connection status
    /// </summary>
    private void UpdateServerDeviceStatus(string deviceId, bool connected)
    {
        var server = ServerList.FirstOrDefault(s => s.SyncthingDeviceId == deviceId);
        if (server == null)
            return;

        if (!connected)
        {
            server.SyncthingStatus = "❌ Offline";
            server.SyncthingCurrentState = "offline";
            // Clear stale folder sync states to prevent data accumulation
            server.ClearAllFolderSyncStates();
        }
        else
        {
            // Device reconnected, refresh status
            _ = Task.Run(async () =>
            {
                await _statusService.RefreshServerStatusOnUIThreadAsync(server, CancellationToken.None);
            });
        }
    }

    #endregion

    [ObservableProperty]
    private string _totalDurationText = string.Empty;

    [ObservableProperty]
    private IEnumerable<ISeries> _durationsSeries = Array.Empty<ISeries>();

    private readonly ObservableCollection<ISeries> _pieSeriesCollection = new();
    private readonly Dictionary<string, (PieSeries<ObservableValue> Series, ObservableValue Value)> _pieSeriesMap = new(StringComparer.Ordinal);

    private readonly ObservableCollection<ISeries> _pipelineSeriesCollection = new();
    private readonly Dictionary<string, (ColumnSeries<ObservablePoint> Series, ObservableCollection<ObservablePoint> Values)> _pipelineSeriesMap = new(StringComparer.Ordinal);

    [ObservableProperty]
    private IEnumerable<ISeries> _pipelineSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private IEnumerable<Axis> _pipelineYAxes = new[] { new Axis() };

    [ObservableProperty]
    private IEnumerable<Axis> _pipelineXAxes = new[] { new Axis() };

    private static readonly HashSet<string> _excludedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "server_status_refresh",
        "startup_operations"
    };

    private static bool IsExcludedOperation(string rawName)
    {
        var baseName = System.Text.RegularExpressions.Regex.Replace(rawName, @"\[.*?\]", "").TrimEnd('_', ' ');
        return _excludedOperations.Contains(baseName);
    }

    private static readonly SKColor[] _chartPalette =
    {
        new SKColor(255, 85,  85),   // red
        new SKColor(85,  170, 255),  // blue
        new SKColor(85,  220, 120),  // green
        new SKColor(255, 165, 0),    // orange
        new SKColor(200, 100, 255),  // purple
        new SKColor(0,   210, 210),  // cyan
        new SKColor(255, 215, 0),    // yellow
        new SKColor(255, 100, 180),  // hot pink
        new SKColor(100, 255, 200),  // mint
        new SKColor(255, 130, 60),   // deep orange
        new SKColor(130, 100, 255),  // indigo
        new SKColor(60,  220, 60),   // lime
    };

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (var c in s.ToLowerInvariant()) { h = h * 31 + c; }
            return Math.Abs(h);
        }
    }

    private static SKColor PickColor(string formattedName, Dictionary<string, SKColor> map)
    {
        if (map.TryGetValue(formattedName, out var c)) { return c; }
        var start = StableHash(formattedName) % _chartPalette.Length;
        var used = new HashSet<SKColor>(map.Values);
        for (int i = 0; i < _chartPalette.Length; i++)
        {
            var candidate = _chartPalette[(start + i) % _chartPalette.Length];
            if (!used.Contains(candidate)) { c = candidate; break; }
        }
        map[formattedName] = c;
        return c;
    }

    [RelayCommand]
    private async Task RefreshMetricsAsync()
    {
        try
        {
            var metrics = await _metricsService.GetMetricsAsync();
            var history = _metricsService.GetOperationHistory();

            // Build shared color map: same name → same color in both charts
            var colorMap = new Dictionary<string, SKColor>();
            if (metrics.TryGetValue("durations", out var durObj) && durObj is Dictionary<string, object> durDict)
            {
                foreach (var k in durDict.Keys.Where(k => !IsExcludedOperation(k)))
                    PickColor(FormatMetricName(k), colorMap);
            }
            foreach (var op in history.Where(op => !IsExcludedOperation(op.Name)))
                PickColor(FormatMetricName(op.Name), colorMap);

            // ── Pie chart ──────────────────────────────────────────────────
            if (!ReferenceEquals(DurationsSeries, _pieSeriesCollection))
            {
                DurationsSeries = _pieSeriesCollection;
            }

            if (metrics.TryGetValue("durations", out var durationsObj) && durationsObj is Dictionary<string, object> durations)
            {
                var activeKeys = new HashSet<string>();
                foreach (var kvp in durations.Where(kvp => !IsExcludedOperation(kvp.Key)))
                {
                    double avg = 0;
                    if (kvp.Value is JsonElement je && je.TryGetProperty("Average", out var avgProp))
                        avg = avgProp.GetDouble();
                    else if (kvp.Value is Dictionary<string, object> dict && dict.TryGetValue("Average", out var avgObj))
                        avg = Convert.ToDouble(avgObj);
                    else
                    {
                        var pi = kvp.Value.GetType().GetProperty("Average");
                        if (pi != null) { avg = Convert.ToDouble(pi.GetValue(kvp.Value)); }
                    }

                    if (avg <= 0) { continue; }

                    var newValue = Math.Round(avg / 1000.0, 2);
                    var name = FormatMetricName(kvp.Key);
                    var color = PickColor(name, colorMap);
                    activeKeys.Add(name);

                    if (_pieSeriesMap.TryGetValue(name, out var existing))
                    {
                        existing.Value.Value = newValue;
                    }
                    else
                    {
                        var obsVal = new ObservableValue(newValue);
                        var pieSeries = new PieSeries<ObservableValue>
                        {
                            Values = new[] { obsVal },
                            Name = name,
                            Fill = new SolidColorPaint(color),
                            DataLabelsSize = 11,
                            DataLabelsPaint = new SolidColorPaint(SKColors.White),
                            DataLabelsPosition = LiveChartsCore.Measure.PolarLabelsPosition.Middle,
                            DataLabelsFormatter = p => DurationFormatter.Format(p.Coordinate.PrimaryValue * 1000),
                            ToolTipLabelFormatter = p => $"{p.Context.Series.Name}  {DurationFormatter.Format(p.Coordinate.PrimaryValue * 1000)}"
                        };
                        _pieSeriesMap[name] = (pieSeries, obsVal);
                        _pieSeriesCollection.Add(pieSeries);
                    }
                }

                foreach (var key in _pieSeriesMap.Keys.Where(k => !activeKeys.Contains(k)).ToList())
                {
                    _pieSeriesCollection.Remove(_pieSeriesMap[key].Series);
                    _pieSeriesMap.Remove(key);
                }
            }

            // ── Pipeline chart ─────────────────────────────────────────────
            var filteredHistory = history
                .Where(op => !IsExcludedOperation(op.Name))
                .ToList();

            var totalMs = filteredHistory.Where(op => op.Success).Sum(op => op.DurationMs);
            TotalDurationText = totalMs > 0 ? $"Total: {DurationFormatter.Format(totalMs)}" : string.Empty;

            if (!ReferenceEquals(PipelineSeries, _pipelineSeriesCollection))
            {
                PipelineSeries = _pipelineSeriesCollection;
            }

            if (filteredHistory.Count > 0)
            {
                var uniqueNames = filteredHistory.Select(op => FormatMetricName(op.Name)).Distinct().ToList();
                var activeKeys = new HashSet<string>(uniqueNames);

                foreach (var opName in uniqueNames)
                {
                    var color = PickColor(opName, colorMap);
                    var newPoints = filteredHistory
                        .Select((op, i) => FormatMetricName(op.Name) == opName && op.Success
                            ? (idx: i, dur: Math.Round(op.DurationMs, 1))
                            : ((int idx, double dur)?)null)
                        .Where(p => p.HasValue)
                        .Select(p => p!.Value)
                        .ToList();

                    if (newPoints.Count == 0) { continue; }

                    if (_pipelineSeriesMap.TryGetValue(opName, out var existing))
                    {
                        var vals = existing.Values;
                        for (int i = 0; i < Math.Min(vals.Count, newPoints.Count); i++)
                        {
                            vals[i].X = newPoints[i].idx;
                            vals[i].Y = newPoints[i].dur;
                        }
                        while (vals.Count > newPoints.Count) { vals.RemoveAt(vals.Count - 1); }
                        for (int i = vals.Count; i < newPoints.Count; i++)
                        {
                            vals.Add(new ObservablePoint(newPoints[i].idx, newPoints[i].dur));
                        }
                    }
                    else
                    {
                        var valuesCollection = new ObservableCollection<ObservablePoint>(
                            newPoints.Select(p => new ObservablePoint(p.idx, p.dur)));
                        var series = new ColumnSeries<ObservablePoint>
                        {
                            Values = valuesCollection,
                            Name = opName,
                            Fill = new SolidColorPaint(color),
                            Stroke = null,
                            MaxBarWidth = 24,
                            IgnoresBarPosition = true,
                            DataLabelsSize = 10,
                            DataLabelsPaint = new SolidColorPaint(SKColors.White),
                            DataLabelsFormatter = p =>
                            {
                                var v = p.Coordinate.PrimaryValue;
                                return v > 0 ? DurationFormatter.Format(v) : string.Empty;
                            }
                        };
                        _pipelineSeriesMap[opName] = (series, valuesCollection);
                        _pipelineSeriesCollection.Add(series);
                    }
                }

                // Error series
                const string errorKey = "__error__";
                var newErrorPoints = filteredHistory
                    .Select((op, i) => !op.Success
                        ? (idx: i, dur: Math.Round(op.DurationMs, 1))
                        : ((int idx, double dur)?)null)
                    .Where(p => p.HasValue)
                    .Select(p => p!.Value)
                    .ToList();

                if (newErrorPoints.Count > 0)
                {
                    activeKeys.Add(errorKey);
                    if (_pipelineSeriesMap.TryGetValue(errorKey, out var existingErr))
                    {
                        var vals = existingErr.Values;
                        for (int i = 0; i < Math.Min(vals.Count, newErrorPoints.Count); i++)
                        {
                            vals[i].X = newErrorPoints[i].idx;
                            vals[i].Y = newErrorPoints[i].dur;
                        }
                        while (vals.Count > newErrorPoints.Count) { vals.RemoveAt(vals.Count - 1); }
                        for (int i = vals.Count; i < newErrorPoints.Count; i++)
                        {
                            vals.Add(new ObservablePoint(newErrorPoints[i].idx, newErrorPoints[i].dur));
                        }
                    }
                    else
                    {
                        var valuesCollection = new ObservableCollection<ObservablePoint>(
                            newErrorPoints.Select(p => new ObservablePoint(p.idx, p.dur)));
                        var series = new ColumnSeries<ObservablePoint>
                        {
                            Values = valuesCollection,
                            Name = "Error",
                            Fill = new SolidColorPaint(new SKColor(220, 80, 80, 210)),
                            Stroke = null,
                            MaxBarWidth = 24,
                            IgnoresBarPosition = true,
                            DataLabelsSize = 10,
                            DataLabelsPaint = new SolidColorPaint(SKColors.White),
                            DataLabelsFormatter = p =>
                            {
                                var v = p.Coordinate.PrimaryValue;
                                return v > 0 ? DurationFormatter.Format(v) : string.Empty;
                            }
                        };
                        _pipelineSeriesMap[errorKey] = (series, valuesCollection);
                        _pipelineSeriesCollection.Add(series);
                    }
                }

                // Remove series no longer present
                foreach (var key in _pipelineSeriesMap.Keys.Where(k => !activeKeys.Contains(k)).ToList())
                {
                    _pipelineSeriesCollection.Remove(_pipelineSeriesMap[key].Series);
                    _pipelineSeriesMap.Remove(key);
                }
                var count = filteredHistory.Count;
                PipelineXAxes = new[]
                {
                    new Axis
                    {
                        Labeler = v =>
                        {
                            var idx = (int)Math.Round(v);
                            return (Math.Abs(v - idx) < 0.01 && idx >= 0 && idx < count)
                                ? (idx + 1).ToString()
                                : string.Empty;
                        },
                        MinStep = 1,
                        ForceStepToMin = true,
                        MinZoomDelta = 1,
                        TextSize = 12
                    }
                };
                var maxDuration = filteredHistory.Where(op => op.Success).Select(op => op.DurationMs).DefaultIfEmpty(0).Max();
                PipelineYAxes = new[]
                {
                    new Axis
                    {
                        Labeler = v => DurationFormatter.Format(v),
                        MinLimit = 0,
                        MaxLimit = maxDuration * 1.3
                    }
                };
            }
            else
            {
                foreach (var key in _pipelineSeriesMap.Keys.ToList())
                {
                    _pipelineSeriesCollection.Remove(_pipelineSeriesMap[key].Series);
                    _pipelineSeriesMap.Remove(key);
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to refresh metrics: {ex.Message}");
        }
    }

    private static string FormatMetricName(string name)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"\[.*?\]", "").TrimEnd('_', ' ');
        if (cleaned.EndsWith("_success", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^"_success".Length];
        }
        cleaned = cleaned.TrimEnd('_');
        return string.Join(" ", cleaned.Split('_')
            .Select(w => string.Equals(w, "iis", StringComparison.OrdinalIgnoreCase)
                ? "IIS"
                : (w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w)));
    }
}
