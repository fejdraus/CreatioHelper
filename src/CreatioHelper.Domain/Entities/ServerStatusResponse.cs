using System;
namespace CreatioHelper.Domain.Entities;

public class ServerStatusResponse
{
    public string ServerName { get; set; } = "";
    public string SiteStatus { get; set; } = "";
    public string PoolStatus { get; set; } = "";
    public DateTime LastChecked { get; set; }
    public string? ErrorMessage { get; set; }
}