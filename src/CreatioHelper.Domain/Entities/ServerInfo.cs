using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.ValueObjects;

namespace CreatioHelper.Domain.Entities;

public class ServerInfo : DtoServerInfo
{
    private string? _serviceName;
    private string _poolStatus = "";
    private string _siteStatus = "";
    private string _serviceStatus = "";
    private bool _isStatusLoading;

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
}
