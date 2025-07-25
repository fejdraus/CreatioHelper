using System;

namespace CreatioHelper.Domain.Entities;

public class ServerInfo
{
    public string Name { get; set; } = "";
    public string NetworkPath { get; set; } = "";
    public string? PoolName { get; set; }
    public string? SiteName { get; set; }
    public string? ServiceName { get; set; }
    public string PoolStatus { get; set; } = "";
    public string SiteStatus { get; set; } = "";
    public string ServiceStatus { get; set; } = "";
    public bool IsStatusLoading { get; set; }
    public Version? AppVersion { get; set; } = new();
}
