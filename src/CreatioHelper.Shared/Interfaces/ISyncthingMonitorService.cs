using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Shared.Interfaces;

/// <summary>
/// Service for monitoring Syncthing synchronization completion for remote servers
/// </summary>
public interface ISyncthingMonitorService
{
    /// <summary>
    /// Wait for synchronization completion for a single server
    /// </summary>
    Task<bool> WaitForServerSyncCompletionAsync(ServerInfo server, CancellationToken cancellationToken);

    /// <summary>
    /// Monitor multiple servers in parallel and invoke callback when each completes
    /// </summary>
    Task<List<ServerInfo>> WaitForMultipleServersAsync(
        List<ServerInfo> servers,
        Action<ServerInfo> onServerCompleted,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get current status of a Syncthing device and folder
    /// Returns status string: "Online", "Offline", "Syncing", "Not Configured", etc.
    /// </summary>
    Task<string> GetDeviceAndFolderStatusAsync(ServerInfo server, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause synchronization for a specific folder
    /// </summary>
    Task<bool> PauseFolderAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume synchronization for a specific folder
    /// </summary>
    Task<bool> ResumeFolderAsync(string folderId, CancellationToken cancellationToken = default);
}
