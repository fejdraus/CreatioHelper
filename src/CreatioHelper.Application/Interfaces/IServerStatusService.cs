using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface IServerStatusService
{
    Task<ServerInfo> RefreshServerStatusAsync(ServerInfo server, CancellationToken cancellationToken = default);
    Task RefreshMultipleServerStatusAsync(ServerInfo[] servers, CancellationToken cancellationToken = default);
    Task<ServerInfo> RefreshServerStatusOnUIThreadAsync(ServerInfo server, CancellationToken cancellationToken = default);
    Task RefreshMultipleServerStatusOnUIThreadAsync(ServerInfo[] servers, CancellationToken cancellationToken = default);
}
