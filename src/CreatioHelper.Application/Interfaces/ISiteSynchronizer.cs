using System.Collections.Generic;
using CreatioHelper.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace CreatioHelper.Application.Interfaces;

public interface ISiteSynchronizer
{
    Task<bool> SynchronizeAsync(
        string sitePath,
        List<ServerInfo> targetServers,
        CancellationToken cancellationToken = default);
}