using System.Collections.Generic;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public class WebServerResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public Data? Data { get; set; }
}

public interface IWebServerService
{
    Task<WebServerResult> StartSiteAsync(string siteName);
    Task<WebServerResult> StopSiteAsync(string siteName);
    Task<WebServerResult> GetSiteStatusAsync(string siteName);
    Task<WebServerResult> StartAppPoolAsync(string poolName);
    Task<WebServerResult> StopAppPoolAsync(string poolName);
    Task<WebServerResult> GetAppPoolStatusAsync(string poolName);
    Task<List<WebServerStatus>> GetAllSitesAsync();
    Task<List<WebServerStatus>> GetAllAppPoolsAsync();
    bool IsSupported();
}

public class Data
{
    public string? ServiceName { get; set; }
    public string? Status { get; set; }
    public string? Details { get; set; }
    public string? PoolName { get; set; }
}