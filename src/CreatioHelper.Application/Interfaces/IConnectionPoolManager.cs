namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Интерфейс для управления пулом соединений к удаленным серверам
/// </summary>
public interface IConnectionPoolManager : System.IDisposable
{
    /// <summary>
    /// Выполняет операцию с использованием соединения из пула
    /// </summary>
    /// <typeparam name="T">Тип возвращаемого результата</typeparam>
    /// <param name="serverName">Имя сервера</param>
    /// <param name="operation">Операция для выполнения</param>
    /// <returns>Результат операции</returns>
    System.Threading.Tasks.Task<T> ExecuteAsync<T>(string serverName, System.Func<IRemoteConnection, System.Threading.Tasks.Task<T>> operation);
}

/// <summary>
/// Интерфейс для удаленного соединения в пуле
/// </summary>
public interface IRemoteConnection : System.IDisposable
{
    /// <summary>
    /// Проверяет, активно ли соединение
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Проверяет, есть ли ошибки в соединении
    /// </summary>
    bool HasErrors { get; }
    
    /// <summary>
    /// Выполняет PowerShell команду на удаленном сервере
    /// </summary>
    /// <param name="command">Команда для выполнения</param>
    /// <returns>Результат выполнения команды</returns>
    System.Threading.Tasks.Task<string> ExecuteCommandAsync(string command);
    
    /// <summary>
    /// Создает новый экземпляр PowerShell для выполнения команд
    /// </summary>
    /// <returns>Экземпляр PowerShell</returns>
    System.Threading.Tasks.Task<object> CreatePowerShellAsync();
}
