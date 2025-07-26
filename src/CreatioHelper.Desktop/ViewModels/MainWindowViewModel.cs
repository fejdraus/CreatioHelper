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
using CreatioHelper.Core;
using CreatioHelper.Core.Services;
using CreatioHelper.Services;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Mediator;
using CreatioHelper.Application.Settings;
using System.Diagnostics;
using System.Threading;

namespace CreatioHelper.ViewModels;

[SupportedOSPlatform("windows")]
public partial class MainWindowViewModel : ObservableObject
{
    private readonly bool _isInitializing;
    private readonly Dictionary<ServerInfo, PropertyChangedEventHandler> _serverHandlers = new();
    private readonly ServerStatusService _statusService;
    private readonly IRemoteIisManager _remoteIisManager;
    private readonly IisService _iisService;
    private readonly IMediator _mediator;
    private readonly IOperationsService _operationsService;
    private readonly IDialogService _dialogService;
    private Version _sitePathWithVersion = new();
    private IOutputWriter _output;
    
    public MainWindowViewModel(IOutputWriter output, IMediator mediator, IOperationsService operationsService, IDialogService dialogService)
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
        _statusService = new ServerStatusService(output);
        _isInitializing = true;
        _remoteIisManager = new RemoteIisManager(output);
        _iisService = new IisService();
        
        var settings = _mediator.Send(new LoadSettingsQuery()).GetAwaiter().GetResult();
        
        if (OperatingSystem.IsWindows())
        {
            LoadIisSites(settings);
        }
        else
        {
            if (settings.IsIisMode)
            {
                IsFolderMode = true;
            }
            ApplyServerSettings(settings);
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
            !ServerList.Any(s => s.Name.Equals(NewServerName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ServerList.Add(new ServerInfo
            {
                Name = NewServerName.Trim(),
                NetworkPath = NewServerName.Trim()
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
    
    [RelayCommand]
    private async Task RefreshServerStatus(ServerInfo server)
    {
        await _statusService.RefreshServerStatusAsync(server);
    }
    
    [RelayCommand]
    private async Task RefreshAllServersStatus()
    {
        if (ServerList.Count == 0) return;
        await _statusService.RefreshMultipleServersStatusAsync(ServerList.ToArray());
    }
    
    [RelayCommand]
    private async Task StopPool(ServerInfo server)
    {
        var result = await _remoteIisManager.StopAppPoolAsync(server);
        if (result)
        {
            await _statusService.RefreshServerStatusAsync(server);
        }
    }

    [RelayCommand]
    private async Task StartPool(ServerInfo server)
    {
        var result = await _remoteIisManager.StartAppPoolAsync(server);
        if (result)
        {
            await _statusService.RefreshServerStatusAsync(server);
        }
    }

    [RelayCommand]
    private async Task StopSite(ServerInfo server)
    {
        var result = await _remoteIisManager.StopWebsiteAsync(server);
        if (result)
        {
            await _statusService.RefreshServerStatusAsync(server);
        }
    }
    
    [ObservableProperty]
    private bool _isServerControlsEnabled = true;

    [RelayCommand]
    private async Task StartSite(ServerInfo server)
    {
        var result = await _remoteIisManager.StartWebsiteAsync(server);
        if (result)
        {
            await _statusService.RefreshServerStatusAsync(server);
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
    partial void OnSitePathChanged(string? value) => SaveServerSettings();
    partial void OnServiceNameChanged(string? value) => SaveServerSettings();
    partial void OnSelectedIisSiteChanged(IisSiteInfo? value) => SaveServerSettings();
    
    [SupportedOSPlatform("windows")]
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
        var result = await _remoteIisManager.StartServiceAsync(server);
        if (result)
        {
            await _statusService.RefreshServerStatusAsync(server);
        }
    }

    [RelayCommand]
    private async Task StopService(ServerInfo server)
    {
        var result = await _remoteIisManager.StopServiceAsync(server);
        if (result)
        {
            await _statusService.RefreshServerStatusAsync(server);
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
            var systemServiceManager = new SystemServiceManager(_output);
            var result = await systemServiceManager.StartServiceAsync(ServiceName);
            
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
            var systemServiceManager = new SystemServiceManager(_output);
            var result = await systemServiceManager.StopServiceAsync(ServiceName);
            
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
}
