using System;
namespace CreatioHelper.Core.Models;

public class ServerStatusInfo
{
    public string ServerName { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string? PoolName { get; set; }
    public string? SiteStatus { get; set; }
    public string? PoolStatus { get; set; }
    public bool IsStatusLoading { get; set; }
    public bool IsHealthy { get; set; }
    public DateTime LastUpdated { get; set; }
    public string? ErrorMessage { get; set; }
}