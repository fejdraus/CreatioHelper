namespace CreatioHelper.Domain.Entities;

public class ServerInfo : DtoServerInfo
{
    private string? _serviceName;
    private string _poolStatus = "";
    private string _siteStatus = "";
    private string _serviceStatus = "";
    private string _syncthingStatus = "";
    private bool _isStatusLoading;

    // Extended Syncthing sync information
    private double _syncthingCompletionPercent = 0;
    private long _syncthingNeedBytes = 0;
    private int _syncthingNeedItems = 0;
    private string _syncthingCurrentState = "idle";
    private string? _syncthingLastSyncedFile;

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
    public int SyncthingNeedItems
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
}
