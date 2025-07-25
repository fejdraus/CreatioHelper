using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Core.Models;

namespace CreatioHelper.Core.Abstractions;

public interface IFileSyncService
{
    Task<SyncResult> SyncAsync(SyncOptions options, CancellationToken cancellationToken = default);
    Task<SyncResult> SyncAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task<bool> ValidatePathAsync(string path);
}