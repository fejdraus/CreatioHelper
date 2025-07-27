using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;

namespace CreatioHelper.Domain.Events;

public class ServerStatusChangedEvent : DomainEvent
{
    public ServerId ServerId { get; }
    public ServerName ServerName { get; }
    public string OldStatus { get; }
    public string NewStatus { get; }

    public ServerStatusChangedEvent(ServerId serverId, ServerName serverName, string oldStatus, string newStatus)
    {
        ServerId = serverId;
        ServerName = serverName;
        OldStatus = oldStatus;
        NewStatus = newStatus;
    }
}

public class ServerStoppedEvent : DomainEvent
{
    public ServerId ServerId { get; }
    public ServerName ServerName { get; }
    public string ComponentType { get; } // "Pool", "Site", "Service"

    public ServerStoppedEvent(ServerId serverId, ServerName serverName, string componentType)
    {
        ServerId = serverId;
        ServerName = serverName;
        ComponentType = componentType;
    }
}

public class ServerStartedEvent : DomainEvent
{
    public ServerId ServerId { get; }
    public ServerName ServerName { get; }
    public string ComponentType { get; } // "Pool", "Site", "Service"

    public ServerStartedEvent(ServerId serverId, ServerName serverName, string componentType)
    {
        ServerId = serverId;
        ServerName = serverName;
        ComponentType = componentType;
    }
}

public class ServerHealthCheckCompletedEvent : DomainEvent
{
    public ServerId ServerId { get; }
    public bool IsHealthy { get; }
    public string? ErrorMessage { get; }

    public ServerHealthCheckCompletedEvent(ServerId serverId, bool isHealthy, string? errorMessage = null)
    {
        ServerId = serverId;
        IsHealthy = isHealthy;
        ErrorMessage = errorMessage;
    }
}
