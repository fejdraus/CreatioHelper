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
    public string Status { get; set; } = "Unknown";
}

public class WebSitesResponse
{
    public WebSiteInfoDto[] Sites { get; set; } = Array.Empty<WebSiteInfoDto>();
}
