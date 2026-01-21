namespace CreatioHelper.Domain.Entities;

public class ServerInfo : DtoServerInfo
{
    private string? _serviceName;
    private string _poolStatus = "";
    private string _siteStatus = "";
    private string _serviceStatus = "";
    private string _syncthingStatus = "";
    private bool _isStatusLoading;

    // Extended Syncthing sync information (aggregated from all folders)
    private double _syncthingCompletionPercent = 0;
    private long _syncthingNeedBytes = 0;
    private long _syncthingNeedItems = 0;
    private string _syncthingCurrentState = "idle";
    private string? _syncthingLastSyncedFile;

    // Dictionary to track individual folder sync states
    private readonly Dictionary<string, FolderSyncState> _folderSyncStates = new();

    /// <summary>
    /// Event raised when invalid sync data is received (for diagnostics/logging)
    /// Parameters: serverName, folderId, needBytes, needItems, completionPercent
    /// </summary>
    public static event Action<string, string, long, long, double>? OnInvalidSyncDataReceived;

    public string? ServiceName
    {
        get => _serviceName;
        set => SetField(ref _serviceName, value);
    }

    public string PoolStatus
    {
        get => _isStatusLoading ? "Loading..." : _poolStatus;
        set => SetField(ref _poolStatus, value);
    }

    public string SiteStatus
    {
        get => _isStatusLoading ? "Loading..." : _siteStatus;
        set => SetField(ref _siteStatus, value);
    }

    public string ServiceStatus
    {
        get => _isStatusLoading ? "Loading..." : _serviceStatus;
        set => SetField(ref _serviceStatus, value);
    }

    public string SyncthingStatus
    {
        get => _isStatusLoading ? "Loading..." : _syncthingStatus;
        set => SetField(ref _syncthingStatus, value);
    }

    public bool IsStatusLoading
    {
        get => _isStatusLoading;
        set
        {
            if (SetField(ref _isStatusLoading, value))
            {
                OnPropertyChanged(nameof(PoolStatus));
                OnPropertyChanged(nameof(SiteStatus));
                OnPropertyChanged(nameof(ServiceStatus));
                OnPropertyChanged(nameof(SyncthingStatus));
            }
        }
    }

    /// <summary>
    /// Syncthing completion percentage (0-100)
    /// </summary>
    public double SyncthingCompletionPercent
    {
        get => _syncthingCompletionPercent;
        set => SetField(ref _syncthingCompletionPercent, value);
    }

    /// <summary>
    /// Bytes remaining to sync
    /// </summary>
    public long SyncthingNeedBytes
    {
        get => _syncthingNeedBytes;
        set => SetField(ref _syncthingNeedBytes, value);
    }

    /// <summary>
    /// Items (files) remaining to sync
    /// </summary>
    public long SyncthingNeedItems
    {
        get => _syncthingNeedItems;
        set => SetField(ref _syncthingNeedItems, value);
    }

    /// <summary>
    /// Current Syncthing folder state (idle, scanning, syncing, error)
    /// </summary>
    public string SyncthingCurrentState
    {
        get => _syncthingCurrentState;
        set => SetField(ref _syncthingCurrentState, value);
    }

    /// <summary>
    /// Last file that was synchronized (for display purposes)
    /// </summary>
    public string? SyncthingLastSyncedFile
    {
        get => _syncthingLastSyncedFile;
        set => SetField(ref _syncthingLastSyncedFile, value);
    }

    /// <summary>
    /// Helper property: returns true if Syncthing is actively syncing
    /// </summary>
    public bool IsSyncthingSyncing => _syncthingCurrentState == "syncing";

    /// <summary>
    /// Helper property: formatted string for remaining data (e.g., "125 MB (50 files)")
    /// </summary>
    public string SyncthingRemainingFormatted
    {
        get
        {
            if (_syncthingNeedBytes == 0 && _syncthingNeedItems == 0)
                return "Up to date";

            var sizeStr = FormatBytes(_syncthingNeedBytes);
            return $"{sizeStr} ({_syncthingNeedItems} {(_syncthingNeedItems == 1 ? "file" : "files")})";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    /// <summary>
    /// Update sync state for a specific folder
    /// </summary>
    public void UpdateFolderSyncState(string folderId, double completionPercent, long needBytes, long needItems, string currentState, string? lastSyncedFile = null)
    {
        // Validate and sanitize input values to prevent overflow/corruption
        // Negative values indicate API errors or data corruption
        if (needBytes < 0 || needItems < 0)
        {
            System.Diagnostics.Debug.WriteLine($"[ServerInfo] WARNING: Invalid sync data for folder '{folderId}': NeedBytes={needBytes}, NeedItems={needItems}. Ignoring update.");
            OnInvalidSyncDataReceived?.Invoke(Name ?? "Unknown", folderId, needBytes, needItems, completionPercent);
            return;
        }

        // Sanity check: completion should be 0-100
        completionPercent = Math.Clamp(completionPercent, 0, 100);

        if (!_folderSyncStates.ContainsKey(folderId))
        {
            _folderSyncStates[folderId] = new FolderSyncState { FolderId = folderId };
        }

        var folderState = _folderSyncStates[folderId];
        folderState.CompletionPercent = completionPercent;
        folderState.NeedBytes = needBytes;
        folderState.NeedItems = needItems;
        folderState.CurrentState = currentState;
        if (lastSyncedFile != null)
        {
            folderState.LastSyncedFile = lastSyncedFile;
        }

        // Recalculate aggregated values
        RecalculateAggregatedSyncState();
    }

    /// <summary>
    /// Remove folder from sync state tracking (e.g., when folder is removed or server disconnected)
    /// </summary>
    public void RemoveFolderSyncState(string folderId)
    {
        if (_folderSyncStates.Remove(folderId))
        {
            RecalculateAggregatedSyncState();
        }
    }

    /// <summary>
    /// Clear all folder sync states (e.g., on disconnect or reset)
    /// </summary>
    public void ClearAllFolderSyncStates()
    {
        _folderSyncStates.Clear();
        SyncthingCompletionPercent = 0;
        SyncthingNeedBytes = 0;
        SyncthingNeedItems = 0;
        SyncthingCurrentState = "idle";
        SyncthingLastSyncedFile = null;
    }

    /// <summary>
    /// Remove folder states that are no longer in the current folder list.
    /// Call this after SyncthingFolderIds is updated to clean up stale states.
    /// </summary>
    public void PruneStaleFolderStates()
    {
        var validFolderIds = SyncthingFolderIds ?? new List<string>();
        var staleIds = _folderSyncStates.Keys
            .Where(id => !validFolderIds.Contains(id))
            .ToList();

        if (staleIds.Count == 0)
            return;

        foreach (var id in staleIds)
        {
            _folderSyncStates.Remove(id);
            System.Diagnostics.Debug.WriteLine($"[ServerInfo] Pruned stale folder state: {id}");
        }

        RecalculateAggregatedSyncState();
    }

    /// <summary>
    /// Recalculate aggregated sync state from all folder states
    /// Uses arithmetic average for completion percentage, sum for bytes/items
    /// </summary>
    private void RecalculateAggregatedSyncState()
    {
        if (_folderSyncStates.Count == 0)
        {
            SyncthingCompletionPercent = 0;
            SyncthingNeedBytes = 0;
            SyncthingNeedItems = 0;
            SyncthingCurrentState = "idle";
            return;
        }

        // Arithmetic average for completion percentage
        SyncthingCompletionPercent = _folderSyncStates.Values.Average(f => f.CompletionPercent);

        // Sum for bytes and items
        SyncthingNeedBytes = _folderSyncStates.Values.Sum(f => f.NeedBytes);
        SyncthingNeedItems = _folderSyncStates.Values.Sum(f => f.NeedItems);

        // Current state: "syncing" if any folder is syncing, "scanning" if any scanning, otherwise "idle"
        if (_folderSyncStates.Values.Any(f => f.CurrentState == "syncing"))
        {
            SyncthingCurrentState = "syncing";
        }
        else if (_folderSyncStates.Values.Any(f => f.CurrentState == "scanning"))
        {
            SyncthingCurrentState = "scanning";
        }
        else if (_folderSyncStates.Values.Any(f => f.CurrentState == "error"))
        {
            SyncthingCurrentState = "error";
        }
        else
        {
            SyncthingCurrentState = "idle";
        }

        // Update last synced file (use the most recent one from any folder)
        var lastFile = _folderSyncStates.Values
            .Where(f => f.LastSyncedFile != null)
            .Select(f => f.LastSyncedFile)
            .LastOrDefault();
        if (lastFile != null)
        {
            SyncthingLastSyncedFile = lastFile;
        }
    }

    /// <summary>
    /// Returns true if ALL folders are fully synced (100% and idle)
    /// </summary>
    public bool AreAllFoldersSynced()
    {
        if (_folderSyncStates.Count == 0)
            return false;

        return _folderSyncStates.Values.All(f => f.IsFullySynced);
    }

    /// <summary>
    /// Get individual folder sync states (for debugging or detailed display)
    /// </summary>
    public IReadOnlyDictionary<string, FolderSyncState> GetFolderSyncStates()
    {
        return _folderSyncStates;
    }
}
