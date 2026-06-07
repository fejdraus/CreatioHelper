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
    private string? _sshHost;
    private int _sshPort = 22;
    private string? _sshUser;
    private string? _sshPassword;
    private string? _sshKeyPath;
    private List<string> _fileCopyFolderPaths = new();
    private List<string> _fileCopyExcludePatterns = new();
    private bool _sshSudoEnabled;


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

    public string? SshHost
    {
        get => _sshHost;
        set => SetField(ref _sshHost, value);
    }

    public int SshPort
    {
        get => _sshPort;
        set => SetField(ref _sshPort, value);
    }

    public string? SshUser
    {
        get => _sshUser;
        set => SetField(ref _sshUser, value);
    }

    public string? SshPassword
    {
        get => _sshPassword;
        set => SetField(ref _sshPassword, value);
    }

    public string? SshKeyPath
    {
        get => _sshKeyPath;
        set => SetField(ref _sshKeyPath, value);
    }

    public List<string> FileCopyFolderPaths
    {
        get => _fileCopyFolderPaths;
        set => SetField(ref _fileCopyFolderPaths, value);
    }

    public List<string> FileCopyExcludePatterns
    {
        get => _fileCopyExcludePatterns;
        set => SetField(ref _fileCopyExcludePatterns, value);
    }

    public bool SshSudoEnabled
    {
        get => _sshSudoEnabled;
        set => SetField(ref _sshSudoEnabled, value);
    }

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
