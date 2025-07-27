using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Domain.Events;
using CreatioHelper.Domain.Specifications;

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

    public ServerInfo() {}

    public string UniqueKey => Name?.Value ?? "Unknown";

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
        get => _poolStatus;
        set => SetField(ref _poolStatus, value);
    }

    public string SiteStatus
    {
        get => _siteStatus;
        set => SetField(ref _siteStatus, value);
    }

    public string ServiceStatus
    {
        get => _serviceStatus;
        set => SetField(ref _serviceStatus, value);
    }

    public bool IsStatusLoading
    {
        get => _isStatusLoading;
        set => SetField(ref _isStatusLoading, value);
    }

    public Version? AppVersion
    {
        get => _appVersion;
        set => SetField(ref _appVersion, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public bool CanBeStopped()
    {
        return !string.IsNullOrEmpty(PoolName);
    }

    public bool IsHealthy()
    {
        return PoolStatus == "Running" && 
               SiteStatus == "Running" &&
               !IsStatusLoading;
    }

    public bool RequiresMaintenance()
    {
        return PoolStatus == "Stopped" || 
               SiteStatus == "Stopped" ||
               ServiceStatus == "Stopped";
    }

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(Name?.Value) && 
               !string.IsNullOrWhiteSpace(NetworkPath?.Value);
    }

    public IEnumerable<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name?.Value))
            errors.Add("Server name is required");

        if (string.IsNullOrWhiteSpace(NetworkPath?.Value))
            errors.Add("Network path is required");

        return errors;
    }

    public override string ToString() => Name?.Value ?? "Unnamed Server";

    public override bool Equals(object? obj)
    {
        return obj is ServerInfo other && 
               string.Equals(Name?.Value, other.Name?.Value, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return Name?.Value?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;
    }
}
