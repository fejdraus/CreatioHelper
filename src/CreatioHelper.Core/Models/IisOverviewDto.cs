using System;
namespace CreatioHelper.Core.Models;

public class IisOverviewDto
{
    public string ServerName { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTime LastUpdated { get; set; }
    public SitesInfoDto Sites { get; set; } = new();
    public AppPoolsInfoDto AppPools { get; set; } = new();
}
