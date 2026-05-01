using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.Enums;

namespace CreatioHelper.ViewModels;

public class SettingsWindowViewModel : INotifyPropertyChanged
{
    private bool _updateCheckEnabled = true;
    private UpdateChannel _updateChannel = UpdateChannel.Beta;
    private string _currentVersion = string.Empty;
    private string? _latestVersion;
    private string? _checkStatus;
    private bool _isCheckInFlight;

    public IReadOnlyList<UpdateChannel> AvailableChannels { get; } = new[] { UpdateChannel.Stable, UpdateChannel.Beta };

    public bool UpdateCheckEnabled
    {
        get => _updateCheckEnabled;
        set
        {
            if (_updateCheckEnabled == value)
            {
                return;
            }
            _updateCheckEnabled = value;
            OnPropertyChanged();
        }
    }

    public UpdateChannel UpdateChannel
    {
        get => _updateChannel;
        set
        {
            if (_updateChannel == value)
            {
                return;
            }
            _updateChannel = value;
            OnPropertyChanged();
        }
    }

    public string CurrentVersion
    {
        get => _currentVersion;
        set
        {
            if (_currentVersion == value)
            {
                return;
            }
            _currentVersion = value;
            OnPropertyChanged();
        }
    }

    public string? LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (_latestVersion == value)
            {
                return;
            }
            _latestVersion = value;
            OnPropertyChanged();
        }
    }

    public string? CheckStatus
    {
        get => _checkStatus;
        set
        {
            if (_checkStatus == value)
            {
                return;
            }
            _checkStatus = value;
            OnPropertyChanged();
        }
    }

    public bool IsCheckInFlight
    {
        get => _isCheckInFlight;
        set
        {
            if (_isCheckInFlight == value)
            {
                return;
            }
            _isCheckInFlight = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
