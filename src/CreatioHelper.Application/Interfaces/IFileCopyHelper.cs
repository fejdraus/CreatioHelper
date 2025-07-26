using CreatioHelper.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace CreatioHelper.Application.Interfaces;

public interface IFileCopyHelper
{
    Task<int> CopyAsync(
        ServerInfo server,
        string sourceDir,
        string destDir,
        CancellationToken cancellationToken = default);
}
