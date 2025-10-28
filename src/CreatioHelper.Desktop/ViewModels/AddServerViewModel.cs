using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;

namespace CreatioHelper.ViewModels;

public class AddServerViewModel : INotifyPropertyChanged
{
    private readonly bool _useSyncthingForSync;
    private readonly bool _enableFileCopySync;

    public ServerInfo Server { get; }

    public AddServerViewModel(ServerInfo? server = null, bool useSyncthingForSync = false, bool enableFileCopySync = false)
    {
        Server = server ?? new ServerInfo();
        _useSyncthingForSync = useSyncthingForSync;
        _enableFileCopySync = enableFileCopySync;
    }

    // Visibility properties based on global sync settings
    public bool IsSyncthingFieldsVisible => _useSyncthingForSync;
    public bool IsFileCopyFieldsVisible => _enableFileCopySync;

    public string ServerName
    {
        get => Server.Name ?? string.Empty;
        set { Server.Name = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public string NetworkPath
    {
        get => Server.NetworkPath ?? string.Empty;
        set { Server.NetworkPath = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public string SiteName
    {
        get => Server.SiteName ?? string.Empty;
        set { Server.SiteName = value; OnPropertyChanged(); }
    }

    public string PoolName
    {
        get => Server.PoolName ?? string.Empty;
        set { Server.PoolName = value; OnPropertyChanged(); }
    }

    public string SyncthingDeviceId
    {
        get => Server.SyncthingDeviceId ?? string.Empty;
        set { Server.SyncthingDeviceId = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public string SyncthingFolderId
    {
        get => Server.SyncthingFolderId ?? string.Empty;
        set { Server.SyncthingFolderId = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool Validate(out string? validationError)
    {
        if (string.IsNullOrWhiteSpace(ServerName))
        {
            validationError = "Please enter the server name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SiteName))
        {
            validationError = "Please enter the site name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(PoolName))
        {
            validationError = "Please enter the pool name.";
            return false;
        }

        // Validate based on global sync type
        if (_enableFileCopySync)
        {
            if (string.IsNullOrWhiteSpace(NetworkPath))
            {
                validationError = "Please enter the network path for File Copy sync.";
                return false;
            }
        }

        if (_useSyncthingForSync)
        {
            if (string.IsNullOrWhiteSpace(SyncthingDeviceId))
            {
                validationError = "Please enter the Syncthing Device ID.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SyncthingFolderId))
            {
                validationError = "Please enter the Syncthing Folder ID.";
                return false;
            }
        }

        validationError = null;
        return true;
    }
}