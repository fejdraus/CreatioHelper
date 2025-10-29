using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.ViewModels;

public class AddServerViewModel : INotifyPropertyChanged
{
    private readonly bool _useSyncthingForSync;
    private readonly bool _enableFileCopySync;
    private string _newFolderId = string.Empty;

    public ServerInfo Server { get; }
    public ObservableCollection<string> SyncthingFolderIds { get; }

    public AddServerViewModel(ServerInfo? server = null, bool useSyncthingForSync = false, bool enableFileCopySync = false)
    {
        Server = server ?? new ServerInfo();
        _useSyncthingForSync = useSyncthingForSync;
        _enableFileCopySync = enableFileCopySync;

        // Initialize ObservableCollection from Server's list
        SyncthingFolderIds = new ObservableCollection<string>(Server.SyncthingFolderIds ?? new List<string>());
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

    /// <summary>
    /// Text box for adding new folder ID
    /// </summary>
    public string NewFolderId
    {
        get => _newFolderId;
        set
        {
            _newFolderId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAddFolder));
        }
    }

    /// <summary>
    /// Can add folder if text is not empty and not already in list
    /// </summary>
    public bool CanAddFolder => !string.IsNullOrWhiteSpace(NewFolderId) && !SyncthingFolderIds.Contains(NewFolderId.Trim());

    /// <summary>
    /// Returns true if there are folders in the list
    /// </summary>
    public bool HasFolders => SyncthingFolderIds.Count > 0;

    /// <summary>
    /// Add new folder ID to the list (at the beginning)
    /// </summary>
    public void AddFolderId()
    {
        if (!CanAddFolder)
            return;

        var folderId = NewFolderId.Trim();
        SyncthingFolderIds.Insert(0, folderId); // Insert at the beginning instead of Add
        NewFolderId = string.Empty;
        OnPropertyChanged(nameof(CanAddFolder));
        OnPropertyChanged(nameof(HasFolders));
    }

    /// <summary>
    /// Remove folder ID from the list
    /// </summary>
    public void RemoveFolderId(string folderId)
    {
        SyncthingFolderIds.Remove(folderId);
        OnPropertyChanged(nameof(CanAddFolder));
        OnPropertyChanged(nameof(HasFolders));
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

            if (SyncthingFolderIds.Count == 0)
            {
                validationError = "Please add at least one Syncthing Folder ID.";
                return false;
            }

            // Copy folder IDs back to Server object before validation completes
            Server.SyncthingFolderIds = new List<string>(SyncthingFolderIds);
        }

        validationError = null;
        return true;
    }
}