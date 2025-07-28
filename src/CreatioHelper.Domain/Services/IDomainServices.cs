namespace CreatioHelper.Domain.Services;

/// <summary>
/// Base interface for domain services containing business logic not tied to a specific entity.
/// </summary>
public interface IDomainService
{
}

/// <summary>
/// Service for server operations within the domain containing cross-entity business logic.
/// </summary>
public interface IServerDomainService : IDomainService
{
    /// <summary>
    /// Determines whether a server can be stopped according to business rules.
    /// </summary>
    bool CanServerBeStopped(string serverName, DateTime currentTime);
    
    /// <summary>
    /// Calculates restart priority for servers.
    /// </summary>
    int CalculateRestartPriority(string serverName, string siteName);
}
