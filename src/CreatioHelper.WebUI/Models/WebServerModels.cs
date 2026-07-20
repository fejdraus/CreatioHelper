namespace CreatioHelper.WebUI.Models;

public enum WebServerKindDto
{
    Auto,
    Iis,
    Service
}

public class WebServerAccessStatusInfo
{
    public bool RequiresElevation { get; set; }
    public string? Message { get; set; }
    public DateTime? Since { get; set; }
}

public class WebSiteInfoDto
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public WebServerKindDto WebServerType { get; set; }
    public string ServiceName { get; set; } = "";
    public bool AutoDiscovered { get; set; }
    public List<string> FolderIds { get; set; } = new();
    public string Status { get; set; } = "Unknown";
    public string AppPool { get; set; } = "";
    public string SiteState { get; set; } = "";
    public string PoolState { get; set; } = "";
    public bool CanManage { get; set; } = true;
    public bool PoolShared { get; set; }
    public bool IsNested { get; set; }
}

public class WebSitesResponse
{
    public WebSiteInfoDto[] Sites { get; set; } = Array.Empty<WebSiteInfoDto>();
}

public class SiteRegistrationDto
{
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "IIS";
    public string ServiceName { get; set; } = "";
    public string AppPool { get; set; } = "";
    public List<string> FolderIds { get; set; } = new();
}

public class DetectSiteResponse
{
    public string? SiteName { get; set; }
    public string AppPool { get; set; } = "";
    public bool RequiresElevation { get; set; }
}

public class DetectBatchResponse
{
    public Dictionary<string, string?> Sites { get; set; } = new();
    public bool RequiresElevation { get; set; }
}

public class DiscoverSitesResponse
{
    public List<DiscoveredSiteDto> Sites { get; set; } = new();
    public bool RequiresElevation { get; set; }
}

public class DiscoveredSiteDto
{
    public string SiteName { get; set; } = "";
    public string AppRootPath { get; set; } = "";
    public List<DiscoveredFolderDto> Folders { get; set; } = new();
}

public class DiscoveredFolderDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}
