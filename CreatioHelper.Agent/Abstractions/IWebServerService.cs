namespace CreatioHelper.Agent.Abstractions;

public class WebServerResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public object? Data { get; set; }
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