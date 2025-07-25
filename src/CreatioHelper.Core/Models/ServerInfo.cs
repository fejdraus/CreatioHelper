using System;
namespace CreatioHelper.Core.Models;

public class ServerInfo
{
    public string Name { get; set; } = "";
    public string NetworkPath { get; set; } = "";
    public string? PoolName { get; set; }
    public string? SiteName { get; set; }
    public string PoolStatus { get; set; } = "";
    public string SiteStatus { get; set; } = "";
    public bool IsStatusLoading { get; set; }
    public Version Version { get; set; } = new();
}