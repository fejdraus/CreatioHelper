namespace CreatioHelper.Agent.Services.SyncEvents;

public static class FolderSyncStates
{
    public static FolderSyncState Parse(string? state) => state?.ToLowerInvariant() switch
    {
        "idle" => FolderSyncState.Idle,
        "scanning" => FolderSyncState.Scanning,
        "scan-waiting" => FolderSyncState.ScanWaiting,
        "syncing" => FolderSyncState.Syncing,
        "sync-waiting" => FolderSyncState.SyncWaiting,
        "sync-preparing" => FolderSyncState.SyncPreparing,
        "cleaning" => FolderSyncState.Cleaning,
        "clean-waiting" => FolderSyncState.CleanWaiting,
        "error" => FolderSyncState.Error,
        "paused" => FolderSyncState.Paused,
        _ => FolderSyncState.Unknown
    };
}
