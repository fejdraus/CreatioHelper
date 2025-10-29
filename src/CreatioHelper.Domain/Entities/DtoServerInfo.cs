using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CreatioHelper.Domain.Entities;

public class DtoServerInfo : INotifyPropertyChanged
{
    private string? _name;
    private string? _networkPath;
    private string? _poolName;
    private string? _siteName;
    private List<string> _syncthingFolderIds = new();
    private string? _syncthingDeviceId;


    public string? Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string? NetworkPath
    {
        get => _networkPath;
        set => SetField(ref _networkPath, value);
    }

    public string? PoolName
    {
        get => _poolName;
        set => SetField(ref _poolName, value);
    }

    public string? SiteName
    {
        get => _siteName;
        set => SetField(ref _siteName, value);
    }

    /// <summary>
    /// List of Syncthing folder IDs for this server (e.g., ["default", "bin-folder"])
    /// Supports multiple folders per server
    /// </summary>
    public List<string> SyncthingFolderIds
    {
        get => _syncthingFolderIds;
        set
        {
            if (SetField(ref _syncthingFolderIds, value))
            {
                OnPropertyChanged(nameof(HasSyncthingConfig));
            }
        }
    }

    /// <summary>
    /// Syncthing device ID for this server (e.g., "XXXXXXX-YYYYYYY-ZZZZZZZ-...")
    /// </summary>
    public string? SyncthingDeviceId
    {
        get => _syncthingDeviceId;
        set
        {
            if (SetField(ref _syncthingDeviceId, value))
            {
                OnPropertyChanged(nameof(HasSyncthingConfig));
            }
        }
    }

    /// <summary>
    /// Returns "Yes" if both Syncthing Device ID and at least one Folder ID are configured, "No" otherwise
    /// </summary>
    public string HasSyncthingConfig =>
        !string.IsNullOrEmpty(SyncthingDeviceId) && SyncthingFolderIds.Count > 0
            ? "Yes"
            : "No";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public override string ToString() => Name ?? "Unnamed Server";

    public override bool Equals(object? obj)
    {
        return obj is DtoServerInfo other && 
               string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return Name?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;
    }
}
