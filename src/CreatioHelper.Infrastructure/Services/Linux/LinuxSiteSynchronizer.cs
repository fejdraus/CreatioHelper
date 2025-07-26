using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Linux;

public class LinuxSiteSynchronizer : ISiteSynchronizer
{
    public Task<bool> SynchronizeAsync(
        string sitePath,
        List<ServerInfo> targetServers,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
