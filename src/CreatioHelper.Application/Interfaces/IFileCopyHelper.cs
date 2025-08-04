using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface IFileCopyHelper
{
    Task<int> CopyAsync(
        ServerInfo server,
        string sourceDir,
        string destDir,
        CancellationToken cancellationToken = default);
}
