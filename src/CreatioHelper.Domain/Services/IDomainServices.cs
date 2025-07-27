namespace CreatioHelper.Domain.Services;

/// <summary>
/// Базовый интерфейс для всех доменных сервисов.
/// Доменные сервисы содержат бизнес-логику, которая не принадлежит конкретной сущности.
/// </summary>
public interface IDomainService
{
}

/// <summary>
/// Сервис для работы с серверными операциями в рамках домена.
/// Содержит бизнес-логику, которая затрагивает несколько сущностей.
/// </summary>
public interface IServerDomainService : IDomainService
{
    /// <summary>
    /// Проверяет, может ли сервер быть остановлен согласно бизнес-правилам
    /// </summary>
    bool CanServerBeStopped(string serverName, DateTime currentTime);
    
    /// <summary>
    /// Вычисляет приоритет перезапуска серверов
    /// </summary>
    int CalculateRestartPriority(string serverName, string siteName);
}
