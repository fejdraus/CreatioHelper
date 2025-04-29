using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Microsoft.Web.Administration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CreatioHelper.Core;

namespace CreatioHelper.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly bool _isInitializing;
    private readonly Dictionary<ServerInfo, PropertyChangedEventHandler> _serverHandlers = new();

    public MainWindowViewModel()
    {
        _isInitializing = true;

        var settings = AppSettingsService.SettingsFileExists()
            ? AppSettingsService.Load()
            : new AppSettings
            {
                IsIisMode = true
            };

        LoadIisSites();
        ApplySettings(settings);

        // Подписываем существующие элементы
        foreach (var server in ServerList)
        {
            var handler = new PropertyChangedEventHandler((_, _) => SaveSettings());
            _serverHandlers[server] = handler;
            server.PropertyChanged += handler;
        }

        // Подписка на добавление/удаление
        ServerList.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (ServerInfo item in e.NewItems)
                {
                    var handler = new PropertyChangedEventHandler((_, _) => SaveSettings());
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
                SaveSettings();
        };

        _isInitializing = false;
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
    partial void OnIsFolderModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIisMode));
        SaveSettings();
    }

    partial void OnIsServerPanelVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ServerPanelButtonText));
        SaveSettings();
    }

    partial void OnPackagesPathChanged(string? value) => SaveSettings();
    partial void OnPackagesToDeleteBeforeChanged(string? value) => SaveSettings();
    partial void OnPackagesToDeleteAfterChanged(string? value) => SaveSettings();
    partial void OnSitePathChanged(string? value) => SaveSettings();
    partial void OnSelectedIisSiteChanged(IisSiteInfo? value) => SaveSettings();

    private void LoadIisSites()
    {
        try
        {
            using var manager = new ServerManager();
            foreach (var site in manager.Sites)
            {
                var app = site.Applications.FirstOrDefault(a => a.Path == "/0");
                var appVdir = app?.VirtualDirectories["/"];
                var rootVdir = site.Applications["/"]?.VirtualDirectories["/"];

                string sitePath = rootVdir?.PhysicalPath ?? "";
                string appPath = appVdir?.PhysicalPath ?? "";

                if (string.IsNullOrEmpty(sitePath) || string.IsNullOrEmpty(appPath))
                    continue;

                if (!File.Exists(Path.Combine(appPath, "Web.config"))) continue;
                if (!File.Exists(Path.Combine(sitePath, "ConnectionStrings.config"))) continue;
                if (!File.Exists(Path.Combine(sitePath, "Web.config"))) continue;

                IisSites.Add(new IisSiteInfo { Name = site.Name, Path = sitePath });
            }

            if (IisSites.Count > 0)
                SelectedIisSite = IisSites[0];
        }
        catch (Exception ex)
        {
            IisSites.Add(new IisSiteInfo { Name = $"[Error loading IIS] {ex.Message}", Path = "" });
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        SitePath = settings.SitePath;
        PackagesPath = settings.PackagesPath;
        PackagesToDeleteBefore = settings.PackagesToDeleteBefore;
        PackagesToDeleteAfter = settings.PackagesToDeleteAfter;

        if (!string.IsNullOrWhiteSpace(settings.SelectedIisSiteName))
        {
            var match = IisSites.FirstOrDefault(x => x.Name == settings.SelectedIisSiteName);
            if (match != null)
                SelectedIisSite = match;
        }

        IsFolderMode = !settings.IsIisMode;
        IsServerPanelVisible = settings.IsServerPanelVisible;

        ServerList.Clear();
        foreach (var server in settings.ServerList)
            ServerList.Add(server);
    }

    private void SaveSettings()
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
