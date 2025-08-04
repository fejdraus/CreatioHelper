using CreatioHelper.Domain.Common;

namespace CreatioHelper.Application.Services;

/// <summary>
/// Base interface for application services coordinating business operations and
/// managing transactions.
/// </summary>
public interface IApplicationService
{
}

/// <summary>
/// Application service for managing servers. Coordinates domain services and
/// repositories.
/// </summary>
public interface IServerApplicationService : IApplicationService
{
    Task<Result> StopServerAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task<Result> StartServerAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<ServerStatusDto>>> GetServerStatusesAsync(CancellationToken cancellationToken = default);
}

public class ServerStatusDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastChecked { get; set; }
}
