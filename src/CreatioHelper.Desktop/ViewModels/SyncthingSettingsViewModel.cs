using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CreatioHelper.ViewModels;

public class SyncthingSettingsViewModel : INotifyPropertyChanged
{
    private bool _enableFileCopySynchronization;
    private bool _useSyncthingForSync;
    private bool _noSynchronization;
    private string? _syncthingApiUrl;
    private string? _syncthingApiKey;

    public bool EnableFileCopySynchronization
    {
        get => _enableFileCopySynchronization;
        set
        {
            if (_enableFileCopySynchronization != value)
            {
                _enableFileCopySynchronization = value;
                OnPropertyChanged();
                if (value)
                {
                    UseSyncthingForSync = false;
                    NoSynchronization = false;
                }
            }
        }
    }

    public bool UseSyncthingForSync
    {
        get => _useSyncthingForSync;
        set
        {
            if (_useSyncthingForSync != value)
            {
                _useSyncthingForSync = value;
                OnPropertyChanged();
                if (value)
                {
                    EnableFileCopySynchronization = false;
                    NoSynchronization = false;
                }
            }
        }
    }

    public bool NoSynchronization
    {
        get => _noSynchronization;
        set
        {
            if (_noSynchronization != value)
            {
                _noSynchronization = value;
                OnPropertyChanged();
                if (value)
                {
                    EnableFileCopySynchronization = false;
                    UseSyncthingForSync = false;
                }
            }
        }
    }

    public string? SyncthingApiUrl
    {
        get => _syncthingApiUrl;
        set
        {
            if (_syncthingApiUrl != value)
            {
                _syncthingApiUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public string? SyncthingApiKey
    {
        get => _syncthingApiKey;
        set
        {
            if (_syncthingApiKey != value)
            {
                _syncthingApiKey = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
