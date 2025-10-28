namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Tracks synchronization state for a single Syncthing folder
/// </summary>
public class FolderSyncState
{
    /// <summary>
    /// Folder ID (e.g., "default", "bin-folder")
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// Completion percentage (0-100)
    /// </summary>
    public double CompletionPercent { get; set; } = 0;

    /// <summary>
    /// Bytes remaining to sync
    /// </summary>
    public long NeedBytes { get; set; } = 0;

    /// <summary>
    /// Items (files) remaining to sync
    /// </summary>
    public int NeedItems { get; set; } = 0;

    /// <summary>
    /// Current folder state (idle, scanning, syncing, error)
    /// </summary>
    public string CurrentState { get; set; } = "idle";

    /// <summary>
    /// Last file that was synchronized in this folder
    /// </summary>
    public string? LastSyncedFile { get; set; }

    /// <summary>
    /// Returns true if this folder is fully synced (100% and valid)
    /// RemoteState "valid" means device is connected and folder is in sync
    /// </summary>
    public bool IsFullySynced => CompletionPercent >= 100.0 && CurrentState == "valid";
}
