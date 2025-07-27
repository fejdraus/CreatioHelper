using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;

namespace CreatioHelper.Application.Interfaces;

public interface IRemoteIisManager
{
    Task<Result> StopAppPoolAsync(ServerId serverId, CancellationToken cancellationToken = default);
    Task<Result> StopWebsiteAsync(ServerId serverId, CancellationToken cancellationToken = default);
    Task<Result> StartAppPoolAsync(ServerId serverId, CancellationToken cancellationToken = default);
    Task<Result> StartWebsiteAsync(ServerId serverId, CancellationToken cancellationToken = default);
    Task<Result> StartServiceAsync(ServerId serverId, CancellationToken cancellationToken = default);
    Task<Result> StopServiceAsync(ServerId serverId, CancellationToken cancellationToken = default);
    Task<Result<string>> GetAppPoolStatusAsync(ServerId serverId, CancellationToken cancellationToken = default);
    Task<Result<string>> GetWebsiteStatusAsync(ServerId serverId, CancellationToken cancellationToken = default);
}
