using System.Collections.Generic;
using CreatioHelper.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace CreatioHelper.Core
{
    public interface IRemoteSynchronizationService
    {
        Task<bool> SynchronizeAsync(
            string sitePath,
            List<ServerInfo> targetServers,
            CancellationToken cancellationToken = default);
    }
}