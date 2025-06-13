using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml;
using Avalonia.Threading;
using Microsoft.Web.Administration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CreatioHelper.Core;
using CreatioHelper.Core.Services;

namespace CreatioHelper.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly bool _isInitializing;
    private readonly Dictionary<ServerInfo, PropertyChangedEventHandler> _serverHandlers = new();
    private readonly ServerStatusService _statusService;
    private readonly IRemoteIisManager _remoteIisManager;
    private const int MaxLogEntries = 1000;
    private Version _sitePathWithVersion = new();
    private IOutputWriter _output;
    public MainWindowViewModel(IOutputWriter output)
    {
        _output = output;
        _statusService = new ServerStatusService(output);
        _isInitializing = true;
        _remoteIisManager = new RemoteIisManager(output);
        var settings = AppSettingsService.SettingsFileExists()
            ? AppSettingsService.Load()
            : new AppSettings
            {
                IsIisMode = true
            };
        LoadIisSites(settings);
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
    private ObservableCollection<string> _logEntries = new();
    
    [ObservableProperty]
    private bool _isLogToFileEnabled;

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
    
    public void ClearLog()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Clear();
        }, DispatcherPriority.Background);
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
    partial void OnSelectedIisSiteChanged(IisSiteInfo? value) => SaveServerSettings();

    private void LoadIisSites(AppSettings? settings)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                using var manager = new ServerManager();
                var sites = manager.Sites.ToList();
                IisSites.Clear();
            
                if (sites.Count == 0)
                {
                    IisSites.Add(new IisSiteInfo { Name = "[No IIS sites found]", Path = "", PoolName = "" });
                    return;
                }
            
                foreach (var site in manager.Sites)
                {
                    var app = site.Applications.FirstOrDefault(a => a.Path == "/0");
                    var appVdir = app?.VirtualDirectories["/"];
                    var rootApp = site.Applications["/"];
                    var rootVdir = rootApp?.VirtualDirectories["/"];
                    string sitePath = rootVdir?.PhysicalPath ?? "";
                    string appPath = appVdir?.PhysicalPath ?? "";
                    string poolName = rootApp?.ApplicationPoolName ?? "";
                    var connectionStrings = Path.Combine(sitePath, "ConnectionStrings.config");
                    if (string.IsNullOrEmpty(sitePath) || string.IsNullOrEmpty(appPath) || string.IsNullOrEmpty(poolName))
                    {
                        continue;
                    }
                    if (!File.Exists(Path.Combine(appPath, "Web.config")))
                    {
                        continue;
                    }
                    if (!File.Exists(connectionStrings))
                    {
                        continue;
                    }
                    if (!File.Exists(Path.Combine(sitePath, "Web.config")))
                    {
                        continue;
                    }
                    var assemblyName = GetAppAssembly.GetAppVersion(appPath);
                    IisSites.Add(new IisSiteInfo {Id = site.Id, Name = site.Name, Path = sitePath, PoolName = poolName, Version = assemblyName});
                }
                if (settings != null) ApplyServerSettings(settings);
            });
            
        }
        catch (Exception ex)
        {
            IisSites.Add(new IisSiteInfo { Name = $"[Error loading IIS] {ex.Message}", Path = "", PoolName = "" });
        }
    }

    private void ApplyServerSettings(AppSettings settings)
    {
        SitePath = settings.SitePath;
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
        if (_isInitializing || !AppSettingsService.SettingsFileExists())
            return;

        var settings = new AppSettings
        {
            SitePath = IsFolderMode ? SitePath : null,
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

        AppSettingsService.Save(settings);
    }
}
