using CreatioHelper.Application.Common;
using CreatioHelper.Domain.Common;

namespace CreatioHelper.Application.Services;

/// <summary>
/// Базовый интерфейс для всех прикладных сервисов.
/// Координирует выполнение бизнес-операций и управляет транзакциями.
/// </summary>
public interface IApplicationService
{
}

/// <summary>
/// Прикладной сервис для управления серверами.
/// Координирует работу с доменными сервисами и репозиториями.
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
