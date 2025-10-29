using System.Collections.ObjectModel;

namespace CreatioHelper.Domain.Entities;

public class AppSettings
{
    public string? SitePath { get; set; }

    public string? ServiceName { get; set; }

    public string? SelectedIisSiteName { get; set; }

    public string? PackagesPath { get; set; }

    public string? PackagesToDeleteBefore { get; set; }

    public string? PackagesToDeleteAfter { get; set; }


    public ObservableCollection<ServerInfo> ServerList { get; set; } = new();

    public bool IsIisMode { get; set; }

    public bool IsServerPanelVisible { get; set; }

    public bool EnableFileCopySynchronization { get; set; } = true;

    /// <summary>
    /// Use external Syncthing application for synchronization instead of built-in sync
    /// </summary>
    public bool UseSyncthingForSync { get; set; } = false;

    /// <summary>
    /// Syncthing REST API URL (e.g., http://localhost:8384)
    /// User must configure this explicitly
    /// </summary>
    public string? SyncthingApiUrl { get; set; }

    /// <summary>
    /// Syncthing REST API key (X-API-Key header)
    /// Found in Syncthing config.xml or GUI -> Actions -> Settings -> API Key
    /// </summary>
    public string? SyncthingApiKey { get; set; }
}
