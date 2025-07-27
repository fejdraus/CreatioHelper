using System.Collections.Concurrent;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Logging;
using CreatioHelper.Application.Interfaces;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

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
/// Реализация удаленного PowerShell соединения для пула
/// </summary>
public class PowerShellRemoteConnection : IRemoteConnection
{
    private readonly string _serverName;
    private Runspace? _runspace;
    private bool _disposed;

    public bool IsConnected => _runspace?.RunspaceStateInfo.State == RunspaceState.Opened;
    public bool HasErrors { get; private set; }

    public PowerShellRemoteConnection(string serverName)
    {
        _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
        InitializeConnection();
    }

    private void InitializeConnection()
    {
        try
        {
            // Для локального сервера используем обычный runspace
            if (_serverName.Equals("localhost", StringComparison.OrdinalIgnoreCase) || 
                _serverName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                _runspace = RunspaceFactory.CreateRunspace();
            }
            else
            {
                // Для удаленного сервера создаем WSMan соединение
                var connectionInfo = new WSManConnectionInfo(
                    new Uri($"http://{_serverName}:5985/wsman"), 
                    "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                    PSCredential.Empty);
                
                _runspace = RunspaceFactory.CreateRunspace(connectionInfo);
            }
            
            _runspace.Open();
            HasErrors = false;
        }
        catch (Exception)
        {
            HasErrors = true;
            _runspace?.Dispose();
            _runspace = null;
        }
    }

    public async Task<string> ExecuteCommandAsync(string command)
    {
        if (_disposed || _runspace == null || !IsConnected)
        {
            HasErrors = true;
            throw new InvalidOperationException("Connection is not available");
        }

        try
        {
            using var powerShell = PowerShell.Create();
            powerShell.Runspace = _runspace;
            powerShell.AddScript(command);

            var results = await Task.Factory.FromAsync(
                powerShell.BeginInvoke(),
                powerShell.EndInvoke);

            if (powerShell.HadErrors)
            {
                HasErrors = true;
                var errors = string.Join("; ", powerShell.Streams.Error.Select(e => e.ToString()));
                throw new InvalidOperationException($"PowerShell execution failed: {errors}");
            }

            return string.Join(Environment.NewLine, results.Select(r => r?.ToString()));
        }
        catch (Exception)
        {
            HasErrors = true;
            throw;
        }
    }

    public Task<object> CreatePowerShellAsync()
    {
        if (_disposed || _runspace == null || !IsConnected)
        {
            HasErrors = true;
            throw new InvalidOperationException("Connection is not available");
        }

        var powerShell = PowerShell.Create();
        powerShell.Runspace = _runspace;
        return Task.FromResult<object>(powerShell);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _runspace?.Close();
            _runspace?.Dispose();
            _disposed = true;
        }
    }
}
