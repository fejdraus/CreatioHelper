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
    private string _newFolderPath = string.Empty;
    private string _newExcludePattern = string.Empty;

    public ServerInfo Server { get; }
    public ObservableCollection<string> SyncthingFolderIds { get; }
    public ObservableCollection<string> FileCopyFolderPaths { get; }
    public ObservableCollection<string> FileCopyExcludePatterns { get; }

    public AddServerViewModel(ServerInfo? server = null, bool useSyncthingForSync = false, bool enableFileCopySync = false)
    {
        Server = server ?? new ServerInfo();
        _useSyncthingForSync = useSyncthingForSync;
        _enableFileCopySync = enableFileCopySync;

        SyncthingFolderIds = new ObservableCollection<string>(Server.SyncthingFolderIds ?? new List<string>());
        FileCopyFolderPaths = new ObservableCollection<string>(Server.FileCopyFolderPaths ?? new List<string>());
        FileCopyExcludePatterns = new ObservableCollection<string>(Server.FileCopyExcludePatterns ?? new List<string>());
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

    public string ServiceName
    {
        get => Server.ServiceName ?? string.Empty;
        set { Server.ServiceName = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public string SshHost
    {
        get => Server.SshHost ?? string.Empty;
        set { Server.SshHost = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public string SshPort
    {
        get => Server.SshPort > 0 ? Server.SshPort.ToString() : "22";
        set
        {
            Server.SshPort = int.TryParse(value, out var port) && port > 0 ? port : 22;
            OnPropertyChanged();
        }
    }

    public string SshUser
    {
        get => Server.SshUser ?? string.Empty;
        set { Server.SshUser = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public string SshPassword
    {
        get => Server.SshPassword ?? string.Empty;
        set { Server.SshPassword = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    public string SshKeyPath
    {
        get => Server.SshKeyPath ?? string.Empty;
        set { Server.SshKeyPath = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
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

    public string NewFolderPath
    {
        get => _newFolderPath;
        set
        {
            _newFolderPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAddFolderPath));
        }
    }

    public bool CanAddFolderPath =>
        !string.IsNullOrWhiteSpace(NewFolderPath) &&
        !FileCopyFolderPaths.Contains(NewFolderPath.Trim());

    public bool HasFolderPaths => FileCopyFolderPaths.Count > 0;

    public void AddFolderPath()
    {
        if (!CanAddFolderPath)
        {
            return;
        }

        FileCopyFolderPaths.Insert(0, NewFolderPath.Trim());
        NewFolderPath = string.Empty;
        OnPropertyChanged(nameof(CanAddFolderPath));
        OnPropertyChanged(nameof(HasFolderPaths));
    }

    public void RemoveFolderPath(string path)
    {
        FileCopyFolderPaths.Remove(path);
        OnPropertyChanged(nameof(CanAddFolderPath));
        OnPropertyChanged(nameof(HasFolderPaths));
    }

    public string NewExcludePattern
    {
        get => _newExcludePattern;
        set
        {
            _newExcludePattern = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAddExcludePattern));
        }
    }

    public bool CanAddExcludePattern =>
        !string.IsNullOrWhiteSpace(NewExcludePattern) &&
        !FileCopyExcludePatterns.Contains(NewExcludePattern.Trim());

    public bool HasExcludePatterns => FileCopyExcludePatterns.Count > 0;

    public void AddExcludePattern()
    {
        if (!CanAddExcludePattern)
        {
            return;
        }

        FileCopyExcludePatterns.Insert(0, NewExcludePattern.Trim());
        NewExcludePattern = string.Empty;
        OnPropertyChanged(nameof(CanAddExcludePattern));
        OnPropertyChanged(nameof(HasExcludePatterns));
    }

    public void RemoveExcludePattern(string pattern)
    {
        FileCopyExcludePatterns.Remove(pattern);
        OnPropertyChanged(nameof(CanAddExcludePattern));
        OnPropertyChanged(nameof(HasExcludePatterns));
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

        if (_enableFileCopySync)
        {
            if (string.IsNullOrWhiteSpace(NetworkPath))
            {
                validationError = "Please enter the remote site path for File Copy sync.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SshHost))
            {
                validationError = "Please enter the SSH host for File Copy sync.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SshUser))
            {
                validationError = "Please enter the SSH username for File Copy sync.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SshPassword) && string.IsNullOrWhiteSpace(SshKeyPath))
            {
                validationError = "Please enter an SSH password or a path to a private key.";
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

            Server.SyncthingFolderIds = new List<string>(SyncthingFolderIds);
        }

        Server.FileCopyFolderPaths = new List<string>(FileCopyFolderPaths);
        Server.FileCopyExcludePatterns = new List<string>(FileCopyExcludePatterns);

        validationError = null;
        return true;
    }
}