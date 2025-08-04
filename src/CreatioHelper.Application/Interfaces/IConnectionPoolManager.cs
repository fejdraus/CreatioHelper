namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Manages a pool of remote connections for server operations.
/// </summary>
public interface IConnectionPoolManager : IDisposable
{
    /// <summary>
    /// Executes an operation using a connection from the pool.
    /// </summary>
    /// <typeparam name="T">Return type of the operation.</typeparam>
    /// <param name="serverName">Target server name.</param>
    /// <param name="operation">Operation to execute.</param>
    /// <returns>Result of the operation.</returns>
    Task<T> ExecuteAsync<T>(string serverName, System.Func<IRemoteConnection, System.Threading.Tasks.Task<T>> operation);
}

/// <summary>
/// Represents a remote connection stored in the pool.
/// </summary>
public interface IRemoteConnection : System.IDisposable
{
    /// <summary>
    /// Indicates whether the connection is active.
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Indicates whether the connection has encountered errors.
    /// </summary>
    bool HasErrors { get; }
    
    /// <summary>
    /// Executes a PowerShell command on the remote server.
    /// </summary>
    /// <param name="command">Command text.</param>
    /// <returns>Command output.</returns>
    Task<string> ExecuteCommandAsync(string command);
    
    /// <summary>
    /// Creates a new PowerShell instance for command execution.
    /// </summary>
    /// <returns>PowerShell instance.</returns>
    Task<object> CreatePowerShellAsync();
}
