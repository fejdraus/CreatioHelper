using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CreatioHelper.Domain.Entities;

public class ServerInfo : INotifyPropertyChanged
{
    private string _name = "";
    private string _networkPath = "";
    private string? _poolName;
    private string? _siteName;
    private string? _serviceName;
    private string _poolStatus = "";
    private string _siteStatus = "";
    private string _serviceStatus = "";
    private bool _isStatusLoading;
    private Version? _appVersion = new();

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string NetworkPath
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

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
