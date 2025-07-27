using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.MacOS;

public class MacOsSiteSynchronizer : ISiteSynchronizer
{
    public Task<bool> SynchronizeAsync(
        string sitePath,
        List<ServerInfo> targetServers,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
