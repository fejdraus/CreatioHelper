using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Relay service for establishing indirect connections between devices
/// Similar to Syncthing's relay protocol for NAT traversal
/// Allows devices behind firewalls to communicate through relay servers
/// </summary>
public class RelayService
{
    private readonly ILogger<RelayService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly List<string> _relayServers;
    private readonly string _currentDeviceId;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Dictionary<string, RelayConnection> _activeConnections = new();
    
    // Default Syncthing-like relay servers
    private static readonly List<string> DefaultRelayServers = new()
    {
        "wss://relay1.syncthing.net:443",
        "wss://relay2.syncthing.net:443"
    };

    public RelayService(
        ILogger<RelayService> logger,
        IConfiguration configuration,
        HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
        
        _currentDeviceId = Environment.GetEnvironmentVariable("Sync__DeviceId") 
            ?? throw new InvalidOperationException("Sync__DeviceId environment variable is required");
            
        _relayServers = _configuration.GetSection("Sync:RelayServers")
            .Get<List<string>>() ?? DefaultRelayServers;
            
        _logger.LogInformation("Relay service initialized for device {DeviceId} with {Count} relay servers", 
            _currentDeviceId, _relayServers.Count);
    }

    /// <summary>
    /// Establish connection to remote device through relay server
    /// Returns true if connection was successful
    /// </summary>
    public async Task<bool> ConnectToDeviceAsync(string deviceId, string? preferredAddress = null)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_activeConnections.ContainsKey(deviceId))
            {
                _logger.LogInformation("Connection to device {DeviceId} already exists", deviceId);
                return true;
            }

            // Try direct connection first
            if (!string.IsNullOrEmpty(preferredAddress) && await TryDirectConnectionAsync(deviceId, preferredAddress))
            {
                return true;
            }

            // Fallback to relay connection
            return await EstablishRelayConnectionAsync(deviceId);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Send data to remote device through established connection
    /// </summary>
    public async Task<bool> SendDataAsync(string deviceId, byte[] data)
    {
        if (!_activeConnections.TryGetValue(deviceId, out var connection))
        {
            _logger.LogWarning("No active connection to device {DeviceId}", deviceId);
            return false;
        }

        try
        {
            return await connection.SendDataAsync(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send data to device {DeviceId}", deviceId);
            
            // Remove failed connection
            await DisconnectFromDeviceAsync(deviceId);
            return false;
        }
    }

    /// <summary>
    /// Disconnect from remote device
    /// </summary>
    public async Task DisconnectFromDeviceAsync(string deviceId)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_activeConnections.TryGetValue(deviceId, out var connection))
            {
                await connection.DisconnectAsync();
                _activeConnections.Remove(deviceId);
                _logger.LogInformation("Disconnected from device {DeviceId}", deviceId);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Get connection status with remote device
    /// </summary>
    public bool IsConnectedToDevice(string deviceId)
    {
        return _activeConnections.ContainsKey(deviceId) && 
               _activeConnections[deviceId].IsConnected;
    }

    /// <summary>
    /// Try direct TCP connection to device
    /// </summary>
    private async Task<bool> TryDirectConnectionAsync(string deviceId, string address)
    {
        try
        {
            // Parse address (tcp://ip:port)
            if (!address.StartsWith("tcp://"))
                return false;
                
            var hostPort = address[6..]; // Remove "tcp://"
            var parts = hostPort.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
                return false;

            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(parts[0], port);
            
            var connection = new DirectConnection(tcpClient, deviceId, _logger);
            _activeConnections[deviceId] = connection;
            
            _logger.LogInformation("Established direct connection to device {DeviceId} at {Address}", 
                deviceId, address);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct connection to {DeviceId} at {Address} failed", deviceId, address);
            return false;
        }
    }

    /// <summary>
    /// Establish connection through relay server
    /// </summary>
    private async Task<bool> EstablishRelayConnectionAsync(string deviceId)
    {
        foreach (var relayServer in _relayServers)
        {
            try
            {
                var connection = await ConnectThroughRelayAsync(relayServer, deviceId);
                if (connection != null)
                {
                    _activeConnections[deviceId] = connection;
                    _logger.LogInformation("Established relay connection to device {DeviceId} through {RelayServer}", 
                        deviceId, relayServer);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect through relay server {RelayServer}", relayServer);
            }
        }

        _logger.LogWarning("Failed to establish any connection to device {DeviceId}", deviceId);
        return false;
    }

    /// <summary>
    /// Connect to device through specific relay server
    /// </summary>
    private async Task<RelayConnection?> ConnectThroughRelayAsync(string relayServer, string targetDeviceId)
    {
        // Simplified relay protocol implementation
        // В реальной реализации здесь был бы WebSocket или TCP connection к relay серверу
        
        _logger.LogDebug("Attempting relay connection to {DeviceId} through {RelayServer}", 
            targetDeviceId, relayServer);

        // Simulate relay handshake
        var handshake = new RelayHandshake
        {
            SourceDeviceId = _currentDeviceId,
            TargetDeviceId = targetDeviceId,
            RequestType = "connect"
        };

        // В реальной реализации здесь был бы настоящий WebSocket connection
        // Пока создаем заглушку для демонстрации архитектуры
        
        await Task.Delay(100); // Simulate network delay
        
        return new RelayConnectionImpl(relayServer, targetDeviceId, _currentDeviceId, _logger);
    }

    public void Dispose()
    {
        foreach (var connection in _activeConnections.Values)
        {
            connection.DisconnectAsync().GetAwaiter().GetResult();
        }
        _activeConnections.Clear();
        _semaphore.Dispose();
    }
}

/// <summary>
/// Base class for device connections
/// </summary>
public abstract class RelayConnection
{
    protected readonly ILogger _logger;
    protected readonly string _deviceId;
    
    public abstract bool IsConnected { get; }
    
    protected RelayConnection(string deviceId, ILogger logger)
    {
        _deviceId = deviceId;
        _logger = logger;
    }
    
    public abstract Task<bool> SendDataAsync(byte[] data);
    public abstract Task DisconnectAsync();
}

/// <summary>
/// Direct TCP connection to device
/// </summary>
public class DirectConnection : RelayConnection
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    
    public override bool IsConnected => _tcpClient?.Connected == true;
    
    public DirectConnection(TcpClient tcpClient, string deviceId, ILogger logger) 
        : base(deviceId, logger)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
    }
    
    public override async Task<bool> SendDataAsync(byte[] data)
    {
        try
        {
            // Send length prefix + data
            var lengthBytes = BitConverter.GetBytes(data.Length);
            await _stream.WriteAsync(lengthBytes);
            await _stream.WriteAsync(data);
            await _stream.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send data through direct connection to {DeviceId}", _deviceId);
            return false;
        }
    }
    
    public override async Task DisconnectAsync()
    {
        try
        {
            _stream?.Close();
            _tcpClient?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during direct connection cleanup for {DeviceId}", _deviceId);
        }
        await Task.CompletedTask;
    }
}

/// <summary>
/// Connection through relay server
/// </summary>
public class RelayConnectionImpl : RelayConnection
{
    private readonly string _relayServer;
    private readonly string _sourceDeviceId;
    private bool _isConnected;
    
    public override bool IsConnected => _isConnected;
    
    public RelayConnectionImpl(string relayServer, string targetDeviceId, string sourceDeviceId, ILogger logger) 
        : base(targetDeviceId, logger)
    {
        _relayServer = relayServer;
        _sourceDeviceId = sourceDeviceId;
        _isConnected = true; // Simplified for demo
    }
    
    public override async Task<bool> SendDataAsync(byte[] data)
    {
        try
        {
            // В реальной реализации здесь была бы отправка через WebSocket к relay серверу
            _logger.LogDebug("Sending {Bytes} bytes to {DeviceId} through relay {RelayServer}", 
                data.Length, _deviceId, _relayServer);
            
            await Task.Delay(10); // Simulate network delay
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send data through relay to {DeviceId}", _deviceId);
            return false;
        }
    }
    
    public override async Task DisconnectAsync()
    {
        _isConnected = false;
        _logger.LogDebug("Disconnected relay connection to {DeviceId} through {RelayServer}", 
            _deviceId, _relayServer);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Relay handshake message
/// </summary>
public class RelayHandshake
{
    public string SourceDeviceId { get; set; } = string.Empty;
    public string TargetDeviceId { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
}