using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Services;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Application.Mediator;
using CreatioHelper.Application.Settings;
using System.Diagnostics;
using System.Threading;
using CreatioHelper.Domain.ValueObjects;
using System.Xml;

namespace CreatioHelper.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly bool _isInitializing;
    private readonly Dictionary<ServerInfo, PropertyChangedEventHandler> _serverHandlers = new();
    private readonly IServerStatusService _statusService;
    private readonly IRemoteIisManager _remoteIisManager;
    private readonly IisService _iisService;
    private readonly IMediator _mediator;
    private readonly IOperationsService _operationsService;
    private readonly IDialogService _dialogService;
    private readonly ISystemServiceManager _systemServiceManager;
    private readonly IRedisManagerFactory _redisManagerFactory;
    private RedisInfo? _redisInfo;
    private Version _sitePathWithVersion = new();
    private IOutputWriter _output;
    
    public MainWindowViewModel(
        IOutputWriter output,
        IMediator mediator,
        IOperationsService operationsService,
        IDialogService dialogService,
        IServerStatusService statusService,
        IRemoteIisManager remoteIisManager,
        IisService iisService,
        ISystemServiceManager systemServiceManager,
        IRedisManagerFactory redisManagerFactory)
    {
        _output = output;
        _mediator = mediator;
        _operationsService = operationsService;
        _operationsService.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IOperationsService.IsBusy))
                OnPropertyChanged(nameof(IsBusy));
            else if (args.PropertyName == nameof(IOperationsService.StartButtonText))
                OnPropertyChanged(nameof(StartButtonText));
            else if (args.PropertyName == nameof(IOperationsService.IsStopButtonEnabled))
                OnPropertyChanged(nameof(IsStopButtonEnabled));
        };
        _dialogService = dialogService;
        _statusService = statusService;
        _isInitializing = true;
        _remoteIisManager = remoteIisManager;
        _iisService = iisService;
        _systemServiceManager = systemServiceManager;
        _redisManagerFactory = redisManagerFactory;
        
        var settings = _mediator.Send(new LoadSettingsQuery()).GetAwaiter().GetResult();
        
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

        foreach (var server in ServerList)
        {
            var handler = new PropertyChangedEventHandler((_, _) => SaveServerSettings());
            _serverHandlers[server] = handler;
            server.PropertyChanged += handler;
        }

        ServerList.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (ServerInfo item in e.NewItems)
                {
                    var handler = new PropertyChangedEventHandler((_, _) => SaveServerSettings());
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
                SaveServerSettings();
        };

        _isInitializing = false;
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
    private bool _isServerControlsEnabled = true;

    [ObservableProperty]
    private string _redisServiceStatus = "";

    [ObservableProperty]
    private string? _redisServiceName;
    
    public bool IsBusy => _operationsService.IsBusy;
    
    public string StartButtonText => _operationsService.StartButtonText;
    
    public bool IsStopButtonEnabled => _operationsService.IsStopButtonEnabled;
    
    public bool HasIisSites => IisSites.Any(site => !string.IsNullOrEmpty(site.Path) && !string.IsNullOrEmpty(site.PoolName));


    public ObservableCollection<IisSiteInfo> IisSites { get; } = new();
    public ObservableCollection<ServerInfo> ServerList { get; } = new();

    [RelayCommand]
    private void AddServer()
    {
        if (!string.IsNullOrWhiteSpace(NewServerName) &&
            !ServerList.Any(s => s.Name?.Equals(NewServerName.Trim(), StringComparison.OrdinalIgnoreCase) == true))
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
    
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshServerStatus(ServerInfo server)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await _statusService.RefreshServerStatusAsync(server);
    }
    
    [RelayCommand]
    private async Task RefreshAllServersStatus()
    {
        if (ServerList.Count == 0) return;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await _statusService.RefreshMultipleServerStatusAsync(ServerList.ToArray());
    }
    
    [RelayCommand]
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
            
            var result = await _remoteIisManager.StopAppPoolAsync(server.PoolName, CancellationToken.None);
            if (result.IsSuccess)
            {
                _output.WriteLine($"[SUCCESS] Application pool '{server.PoolName}' stopped successfully on server '{server.Name ?? "Unknown"}'.");
                if (OperatingSystem.IsWindows())
                {
                    await _statusService.RefreshServerStatusAsync(server);
                }
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to stop pool: {result.ErrorMessage}");
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

    [RelayCommand]
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
            
            var result = await _remoteIisManager.StartAppPoolAsync(server.PoolName, CancellationToken.None);
            if (result.IsSuccess)
            {
                _output.WriteLine($"[SUCCESS] Application pool '{server.PoolName}' started successfully on server '{server.Name ?? "Unknown"}'.");
                if (OperatingSystem.IsWindows())
                {
                    await _statusService.RefreshServerStatusAsync(server);
                }
            }
            else
            {
                _output.WriteLine($"[ERROR] Failed to start pool: {result.ErrorMessage}");
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

    [RelayCommand]
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
            
            var result = await _remoteIisManager.StopWebsiteAsync(server.SiteName, CancellationToken.None);
            if (result.IsSuccess)
            {
                _output.WriteLine($"[SUCCESS] Website '{server.SiteName}' stopped successfully on server '{server.Name ?? "Unknown"}'.");
                if (OperatingSystem.IsWindows())
                {
                    await _statusService.RefreshServerStatusAsync(server);
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

    [RelayCommand]
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
            
            var result = await _remoteIisManager.StartWebsiteAsync(server.SiteName, CancellationToken.None);
            if (result.IsSuccess)
            {
                _output.WriteLine($"[SUCCESS] Website '{server.SiteName}' started successfully on server '{server.Name ?? "Unknown"}'.");
                if (OperatingSystem.IsWindows())
                {
                    await _statusService.RefreshServerStatusAsync(server);
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
        await _operationsService.StartOperation(this);
    }

    [RelayCommand]
    private void Stop() 
    {
        _operationsService.StopOperation();
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

    private void SetControlsEnabled(bool isEnabled) 
    {
        IsServerControlsEnabled = isEnabled;
    }

    partial void OnIsFolderModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIisMode));
        SaveServerSettings();
    }

    partial void OnIsServerPanelVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ServerPanelButtonText));
        SaveServerSettings();
    }

    partial void OnPackagesPathChanged(string? value) => SaveServerSettings();
    partial void OnPackagesToDeleteBeforeChanged(string? value) => SaveServerSettings();
    partial void OnPackagesToDeleteAfterChanged(string? value) => SaveServerSettings();
    partial void OnSitePathChanged(string? value)
    {
        SaveServerSettings();
        LoadRedisConnectionInfo();
    }
    partial void OnServiceNameChanged(string? value) => SaveServerSettings();
    partial void OnSelectedIisSiteChanged(IisSiteInfo? value)
    {
        SaveServerSettings();
        LoadRedisConnectionInfo();
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

        ServerList.Clear();
        foreach (var server in settings.ServerList)
            ServerList.Add(server);

        LoadRedisConnectionInfo();
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
            PackagesToDeleteBefore = PackagesToDeleteBefore,
            PackagesToDeleteAfter = PackagesToDeleteAfter,
            ServerList = new ObservableCollection<ServerInfo>(ServerList.Select(s => new ServerInfo
            {
                Name = s.Name,
                NetworkPath = s.NetworkPath,
                PoolName = s.PoolName,
                SiteName = s.SiteName
            })),
            IsIisMode = IsIisMode,
            IsServerPanelVisible = IsServerPanelVisible
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

        var result = await _remoteIisManager.StartServiceAsync(server.ServiceName, CancellationToken.None);
        if (result.IsSuccess)
        {
            await _statusService.RefreshServerStatusAsync(server);
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

        var result = await _remoteIisManager.StopServiceAsync(server.ServiceName, CancellationToken.None);
        if (result.IsSuccess)
        {
            await _statusService.RefreshServerStatusAsync(server);
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

    private static (RedisInfo Info, string ServiceName) ReadRedisConnectionInfo(string configPath)
    {
        var info = new RedisInfo();
        string serviceName = "redis";

        try
        {
            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath);
            var node = xmlDoc.SelectSingleNode("/connectionStrings/add[@name='redis']") as XmlElement;
            if (node != null)
            {
                var conn = node.GetAttribute("connectionString");
                info = ParseRedisConnectionString(conn);
                var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 2 && kv[0].Trim().Equals("ServiceName", StringComparison.OrdinalIgnoreCase))
                    {
                        serviceName = kv[1].Trim();
                        break;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return (info, serviceName);
    }

    private static RedisInfo ParseRedisConnectionString(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string host = "localhost";
        string[] clusterHosts = [];
        string port = "6379";
        string db = "0";
        string password = "";
        bool useTls = false;
        string certificatePath = "";
        string certificatePassword = "";

        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2);
            if (keyValue.Length != 2) continue;

            string key = keyValue[0].Trim().ToLowerInvariant();
            string value = keyValue[1].Trim();

            switch (key)
            {
                case "host":
                    host = value;
                    break;
                case "clusterhosts":
                    clusterHosts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "port":
                    port = value;
                    break;
                case "db":
                    db = value;
                    break;
                case "password":
                    password = value;
                    break;
                case "usetls":
                    useTls = bool.TryParse(value, out var tls) && tls;
                    break;
                case "certificatepath":
                    certificatePath = value;
                    break;
                case "certificatepassword":
                    certificatePassword = value;
                    break;
            }
        }

        return new RedisInfo
        {
            Hosts = clusterHosts.Length > 0 ? clusterHosts : new[] { $"{host}:{port}" },
            DataBase = db,
            Password = password,
            UseTls = useTls,
            CertificatePath = certificatePath,
            CertificatePassword = certificatePassword
        };
    }

    private string? GetRedisServiceName()
    {
        if (!string.IsNullOrWhiteSpace(RedisServiceName))
            return RedisServiceName;

        LoadRedisConnectionInfo();
        return RedisServiceName;
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
    private async Task CheckRedisStatus()
    {
        var service = GetRedisServiceName();
        if (service == null)
        {
            _output.WriteLine("[ERROR] Could not locate ConnectionStrings.config");
            RedisServiceStatus = "unknown";
            return;
        }
        var state = await _systemServiceManager.GetServiceStateAsync(service);
        RedisServiceStatus = state ?? "unknown";
        _output.WriteLine($"[INFO] Redis service '{service}' status: {RedisServiceStatus}");
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

    private void LoadRedisConnectionInfo()
    {
        string? configPath = null;
        if (IsFolderMode && !string.IsNullOrWhiteSpace(SitePath))
            configPath = Path.Combine(SitePath, "ConnectionStrings.config");
        else if (IsIisMode && !string.IsNullOrWhiteSpace(SelectedIisSite?.Path))
            configPath = Path.Combine(SelectedIisSite!.Path, "ConnectionStrings.config");

        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            _redisInfo = null;
            RedisServiceName = null;
            RedisServiceStatus = string.Empty;
            return;
        }

        var result = ReadRedisConnectionInfo(configPath);
        _redisInfo = result.Info;
        RedisServiceName = result.ServiceName;
    }
}
