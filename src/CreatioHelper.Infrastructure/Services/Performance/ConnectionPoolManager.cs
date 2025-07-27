using System.Collections.Concurrent;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Logging;
using CreatioHelper.Application.Interfaces;
using System.Management.Automation;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Менеджер пула соединений для PowerShell/SSH операций
/// Снижает накладные расходы на создание новых соединений к серверам
/// </summary>
public class ConnectionPoolManager : IConnectionPoolManager
{
    private readonly ConcurrentDictionary<string, ObjectPool<IRemoteConnection>> _pools = new();
    private readonly IMetricsService _metrics;
    private readonly ILogger<ConnectionPoolManager> _logger;

    public ConnectionPoolManager(IMetricsService metrics, ILogger<ConnectionPoolManager> logger)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<T> ExecuteAsync<T>(string serverName, Func<IRemoteConnection, Task<T>> operation)
    {
        if (string.IsNullOrEmpty(serverName))
            throw new ArgumentException("Server name cannot be null or empty", nameof(serverName));

        var pool = _pools.GetOrAdd(serverName, CreatePool);
        var connection = pool.Get();
        
        try
        {
            return await _metrics.MeasureAsync($"pooled_remote_operation_{serverName}", 
                () => operation(connection));
        }
        finally
        {
            pool.Return(connection);
        }
    }

    private ObjectPool<IRemoteConnection> CreatePool(string serverName)
    {
        _logger.LogDebug("Creating connection pool for server {ServerName}", serverName);
        
        var policy = new RemoteConnectionPoolPolicy(serverName, _logger);
        var provider = new DefaultObjectPoolProvider 
        { 
            MaximumRetained = 5 // Максимум 5 соединений в пуле
        };
        
        return provider.Create(policy);
    }

    public void Dispose()
    {
        foreach (var pool in _pools.Values)
        {
            if (pool is IDisposable disposablePool)
                disposablePool.Dispose();
        }
        _pools.Clear();
    }
}

/// <summary>
/// Политика создания и валидации соединений в пуле
/// </summary>
public class RemoteConnectionPoolPolicy : IPooledObjectPolicy<IRemoteConnection>
{
    private readonly string _serverName;
    private readonly ILogger _logger;

    public RemoteConnectionPoolPolicy(string serverName, ILogger logger)
    {
        _serverName = serverName;
        _logger = logger;
    }

    public IRemoteConnection Create()
    {
        _logger.LogDebug("Creating new PowerShell connection for server {ServerName}", _serverName);
        return new PowerShellRemoteConnection(_serverName);
    }

    public bool Return(IRemoteConnection obj)
    {
        // Проверяем, что соединение еще активно и можно переиспользовать
        return obj.IsConnected && !obj.HasErrors;
    }
}

/// <summary>
/// Интерфейс для удаленного соединения
/// </summary>
public interface IRemoteConnection : IDisposable
{
    bool IsConnected { get; }
    bool HasErrors { get; }
    Task<string> ExecuteCommandAsync(string command);
    Task<PowerShell> CreatePowerShellAsync();
}

/// <summary>
/// Реализация PowerShell соединения для пула
/// </summary>
public class PowerShellRemoteConnection : IRemoteConnection
{
    private readonly string _serverName;
    private PowerShell? _powerShell;
    private bool _disposed;

    public PowerShellRemoteConnection(string serverName)
    {
        _serverName = serverName;
        _powerShell = PowerShell.Create();
    }

    public bool IsConnected => _powerShell != null && !_disposed;
    public bool HasErrors => _powerShell?.HadErrors ?? true;

    public async Task<string> ExecuteCommandAsync(string command)
    {
        if (_disposed || _powerShell == null)
            throw new ObjectDisposedException(nameof(PowerShellRemoteConnection));

        _powerShell.Commands.Clear();
        _powerShell.AddScript(command);
        
        var results = await Task.Run(() => _powerShell.Invoke());
        return string.Join(Environment.NewLine, results.Select(r => r.ToString()));
    }

    public async Task<PowerShell> CreatePowerShellAsync()
    {
        return await Task.FromResult(_powerShell ?? PowerShell.Create());
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _powerShell?.Dispose();
            _powerShell = null;
            _disposed = true;
        }
    }
}

public interface IConnectionPoolManager : IDisposable
{
    Task<T> ExecuteAsync<T>(string serverName, Func<IRemoteConnection, Task<T>> operation);
}
