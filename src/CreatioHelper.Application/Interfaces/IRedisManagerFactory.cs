namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Фабрика для создания менеджеров Redis.
/// </summary>
public interface IRedisManagerFactory
{
    IRedisManager Create(string sitePath);
}
