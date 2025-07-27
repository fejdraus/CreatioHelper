using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Domain.Events;
using CreatioHelper.Domain.Specifications;

namespace CreatioHelper.Domain.Entities;

public class ServerInfo : AggregateRoot, INotifyPropertyChanged
{
    private ServerName _name = new("Default");
    private NetworkPath _networkPath = new("C:\\");
    private string? _poolName;
    private string? _siteName;
    private string? _serviceName;
    private string _poolStatus = "";
    private string _siteStatus = "";
    private string _serviceStatus = "";
    private bool _isStatusLoading;
    private Version? _appVersion = new();

    // Конструкторы
    public ServerInfo(ServerId id, ServerName name, NetworkPath networkPath) : base(id.Value)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _networkPath = networkPath ?? throw new ArgumentNullException(nameof(networkPath));
    }

    public ServerInfo() : base(ServerId.Create().Value) 
    {
        // Id уже установлен в базовом конструкторе
    }

    // Свойства с типобезопасными Value Objects
    public new ServerId Id => new(base.Id);

    public ServerName Name
    {
        get => _name;
        set 
        { 
            if (SetField(ref _name, value ?? throw new ArgumentNullException(nameof(value))))
            {
                // Доменное событие при изменении имени сервера
                Apply(new ServerStatusChangedEvent(Id, _name, "Name", value));
            }
        }
    }

    public NetworkPath NetworkPath
    {
        get => _networkPath;
        set 
        { 
            SetField(ref _networkPath, value ?? throw new ArgumentNullException(nameof(value))); 
        }
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
        set 
        { 
            var oldStatus = _poolStatus;
            if (SetField(ref _poolStatus, value))
            {
                // Доменное событие при изменении статуса
                Apply(new ServerStatusChangedEvent(Id, Name, oldStatus, value));
                
                // Специфичные события для остановки/запуска
                if (oldStatus == "Running" && value == "Stopped")
                    Apply(new ServerStoppedEvent(Id, Name, "Pool"));
                else if (oldStatus == "Stopped" && value == "Running")
                    Apply(new ServerStartedEvent(Id, Name, "Pool"));
            }
        }
    }

    public string SiteStatus
    {
        get => _siteStatus;
        set 
        { 
            var oldStatus = _siteStatus;
            if (SetField(ref _siteStatus, value))
            {
                Apply(new ServerStatusChangedEvent(Id, Name, oldStatus, value));
                
                if (oldStatus == "Running" && value == "Stopped")
                    Apply(new ServerStoppedEvent(Id, Name, "Site"));
                else if (oldStatus == "Stopped" && value == "Running")
                    Apply(new ServerStartedEvent(Id, Name, "Site"));
            }
        }
    }

    public string ServiceStatus
    {
        get => _serviceStatus;
        set 
        { 
            var oldStatus = _serviceStatus;
            if (SetField(ref _serviceStatus, value))
            {
                Apply(new ServerStatusChangedEvent(Id, Name, oldStatus, value));
                
                if (oldStatus == "Running" && value == "Stopped")
                    Apply(new ServerStoppedEvent(Id, Name, "Service"));
                else if (oldStatus == "Stopped" && value == "Running")
                    Apply(new ServerStartedEvent(Id, Name, "Service"));
            }
        }
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

    // Доменные методы с бизнес-логикой
    public bool CanBeStopped(DateTime currentTime)
    {
        var specification = new ServerCanBeStoppedSpecification(currentTime);
        return specification.IsSatisfiedBy(this);
    }

    public bool IsHealthy()
    {
        var specification = new ServerIsHealthySpecification();
        return specification.IsSatisfiedBy(this);
    }

    public bool RequiresMaintenance()
    {
        var specification = new ServerRequiresMaintenanceSpecification();
        return specification.IsSatisfiedBy(this);
    }

    public void UpdateHealthStatus(bool isHealthy, string? errorMessage = null)
    {
        Apply(new ServerHealthCheckCompletedEvent(Id, isHealthy, errorMessage));
    }

    // Реализация AggregateRoot
    public override bool IsValid()
    {
        return GetBrokenRules().Any() == false;
    }

    public override IEnumerable<string> GetBrokenRules()
    {
        var rules = new List<string>();

        if (string.IsNullOrWhiteSpace(Name.Value))
            rules.Add("Server name is required");

        if (string.IsNullOrWhiteSpace(NetworkPath.Value))
            rules.Add("Network path is required");

        if (_isStatusLoading && DomainEvents.Any() && 
            (DateTime.UtcNow - DomainEvents.Last().OccurredOn).TotalMinutes > 5)
            rules.Add("Status loading has been running too long");

        return rules;
    }
}
