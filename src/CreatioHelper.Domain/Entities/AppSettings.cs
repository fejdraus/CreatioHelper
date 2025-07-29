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
}
