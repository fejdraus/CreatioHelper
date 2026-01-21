using CreatioHelper.WebUI.Models;

namespace CreatioHelper.WebUI.Services;

/// <summary>
/// Shared state container for cross-component communication
/// </summary>
public class StateContainer
{
    // System status
    private SystemStatus? _systemStatus;
    public SystemStatus? SystemStatus
    {
        get => _systemStatus;
        set
        {
            _systemStatus = value;
            NotifyStateChanged();
        }
    }

    // Current device ID (from SystemStatus.MyId)
    public string? MyDeviceId => _systemStatus?.MyId;

    // Folders
    private FolderConfig[] _folders = [];
    public FolderConfig[] Folders
    {
        get => _folders;
        set
        {
            _folders = value;
            NotifyStateChanged();
        }
    }

    private readonly Dictionary<string, FolderStatus> _folderStatuses = new();
    public IReadOnlyDictionary<string, FolderStatus> FolderStatuses => _folderStatuses;

    public void UpdateFolderStatus(FolderStatus status)
    {
        _folderStatuses[status.Folder] = status;
        NotifyStateChanged();
    }

    // Devices
    private DeviceConfig[] _devices = [];
    public DeviceConfig[] Devices
    {
        get => _devices;
        set
        {
            _devices = value;
            NotifyStateChanged();
        }
    }

    private readonly Dictionary<string, ConnectionInfo> _connections = new();
    public IReadOnlyDictionary<string, ConnectionInfo> Connections => _connections;

    public void UpdateConnection(ConnectionInfo connection)
    {
        _connections[connection.DeviceId] = connection;
        NotifyStateChanged();
    }

    // Events
    private readonly List<SyncEvent> _recentEvents = new();
    public IReadOnlyList<SyncEvent> RecentEvents => _recentEvents;

    public void AddEvent(SyncEvent evt)
    {
        _recentEvents.Insert(0, evt);
        if (_recentEvents.Count > 100)
        {
            _recentEvents.RemoveAt(_recentEvents.Count - 1);
        }
        NotifyStateChanged();
    }

    public void ClearEvents()
    {
        _recentEvents.Clear();
        NotifyStateChanged();
    }

    // Connection state
    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            _isConnected = value;
            NotifyStateChanged();
        }
    }

    // Loading state
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            NotifyStateChanged();
        }
    }

    // Selected folder (for detail views)
    private string? _selectedFolderId;
    public string? SelectedFolderId
    {
        get => _selectedFolderId;
        set
        {
            _selectedFolderId = value;
            NotifyStateChanged();
        }
    }

    // Selected device (for detail views)
    private string? _selectedDeviceId;
    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set
        {
            _selectedDeviceId = value;
            NotifyStateChanged();
        }
    }

    // Transfer statistics
    private TransferStats _transferStats = new();
    public TransferStats TransferStats
    {
        get => _transferStats;
        set
        {
            _transferStats = value;
            NotifyStateChanged();
        }
    }

    // State change event
    public event Action? OnChange;

    private void NotifyStateChanged() => OnChange?.Invoke();
}

/// <summary>
/// Transfer statistics for dashboard
/// </summary>
public class TransferStats
{
    public long TotalBytesIn { get; set; }
    public long TotalBytesOut { get; set; }
    public double CurrentRateIn { get; set; }
    public double CurrentRateOut { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
