using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;

namespace CreatioHelper.Application.Interfaces;

public interface IRemoteIisManager
{
    Task<Result> StopAppPoolAsync(string poolName, CancellationToken cancellationToken = default);
    Task<Result> StopWebsiteAsync(string siteName, CancellationToken cancellationToken = default);
    Task<Result> StartAppPoolAsync(string poolName, CancellationToken cancellationToken = default);
    Task<Result> StartWebsiteAsync(string siteName, CancellationToken cancellationToken = default);
    Task<Result> StartServiceAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<Result> StopServiceAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<Result<string>> GetAppPoolStatusAsync(string poolName, CancellationToken cancellationToken = default);
    Task<Result<string>> GetWebsiteStatusAsync(string siteName, CancellationToken cancellationToken = default);
}
