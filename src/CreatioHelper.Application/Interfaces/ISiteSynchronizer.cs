using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface ISiteSynchronizer
{
    Task<bool> SynchronizeAsync(
        string sitePath,
        List<ServerInfo> targetServers,
        CancellationToken cancellationToken = default);
}