using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.ValueObjects;

namespace CreatioHelper.Domain.Entities;

public class ServerInfo : INotifyPropertyChanged
{
    private ServerName? _name;
    private NetworkPath? _networkPath;
    private string? _poolName;
    private string? _siteName;
    private string? _serviceName;
    private string _poolStatus = "";
    private string _siteStatus = "";
    private string _serviceStatus = "";
    private bool _isStatusLoading;
    private Version? _appVersion = new();
    private bool _isOnline;
    private DateTime? _lastUpdated;

    public ServerName? Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public NetworkPath? NetworkPath
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
            }
        }
    }

    public Version? AppVersion
    {
        get => _appVersion;
        set => SetField(ref _appVersion, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        set => SetField(ref _isOnline, value);
    }

    public DateTime? LastUpdated
    {
        get => _lastUpdated;
        set => SetField(ref _lastUpdated, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public override string ToString() => Name?.Value ?? "Unnamed Server";

    public override bool Equals(object? obj)
    {
        return obj is ServerInfo other && 
               string.Equals(Name?.Value, other.Name?.Value, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return Name?.Value.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;
    }
}
