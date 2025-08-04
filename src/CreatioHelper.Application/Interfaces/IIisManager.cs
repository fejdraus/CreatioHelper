using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Unified IIS management interface supporting both local and remote servers
/// </summary>
public interface IIisManager
{
    // App Pool Management
    Task<Result> StartAppPoolAsync(string serverName, string poolName, CancellationToken cancellationToken = default);
    Task<Result> StopAppPoolAsync(string serverName, string poolName, CancellationToken cancellationToken = default);
    Task<Result<string>> GetAppPoolStatusAsync(string serverName, string poolName, CancellationToken cancellationToken = default);
    
    // Website Management  
    Task<Result> StartWebsiteAsync(string serverName, string siteName, CancellationToken cancellationToken = default);
    Task<Result> StopWebsiteAsync(string serverName, string siteName, CancellationToken cancellationToken = default);
    Task<Result<string>> GetWebsiteStatusAsync(string serverName, string siteName, CancellationToken cancellationToken = default);
    
    // Service Management
    Task<Result> StartServiceAsync(string serverName, string serviceName, CancellationToken cancellationToken = default);
    Task<Result> StopServiceAsync(string serverName, string serviceName, CancellationToken cancellationToken = default);
    
    // Bulk Operations (for local server only)
    Task<List<WebServerStatus>> GetAllSitesAsync();
    Task<List<WebServerStatus>> GetAllAppPoolsAsync();
    
    // Utility
    bool IsSupported();
    bool IsLocal(string serverName);
}