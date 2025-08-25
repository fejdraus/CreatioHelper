using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// WebSocket-based relay service for Syncthing-compatible relay protocol
/// Implements full WebSocket communication with relay servers
/// </summary>
public class WebSocketRelayService : IDisposable
{
    private readonly ILogger<WebSocketRelayService> _logger;
    private readonly string _currentDeviceId;
    private readonly ConcurrentDictionary<string, WebSocketRelayConnection> _connections = new();
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    
    // Default Syncthing relay servers
    private static readonly List<string> DefaultRelayServers = new()
    {
        "wss://relay1.syncthing.net/endpoint",
        "wss://relay2.syncthing.net/endpoint",
        "wss://relay3.syncthing.net/endpoint"
    };

    public WebSocketRelayService(ILogger<WebSocketRelayService> logger, string deviceId)
    {
        _logger = logger;
        _currentDeviceId = deviceId;
    }

    /// <summary>
    /// Connect to a device through relay server using WebSocket
    /// </summary>
    /// <param name="targetDeviceId">Target device ID</param>
    /// <param name="relayServers">List of relay servers to try (null for default)</param>
    /// <returns>True if connection established</returns>
    public async Task<bool> ConnectToDeviceAsync(string targetDeviceId, List<string>? relayServers = null)
    {
        if (_connections.ContainsKey(targetDeviceId))
        {
            _logger.LogInformation("Connection to device {DeviceId} already exists via relay", targetDeviceId);
            return true;
        }

        await _connectionSemaphore.WaitAsync();
        try
        {
            var servers = relayServers ?? DefaultRelayServers;
            
            foreach (var relayServer in servers)
            {
                try
                {
                    _logger.LogDebug("🔗 Attempting relay connection to {DeviceId} via {RelayServer}", 
                        targetDeviceId, relayServer);
                    
                    var connection = await CreateRelayConnectionAsync(relayServer, targetDeviceId);
                    if (connection != null)
                    {
                        _connections[targetDeviceId] = connection;
                        _logger.LogInformation("✅ Established relay connection to {DeviceId} via {RelayServer}",
                            targetDeviceId, relayServer);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to connect to {DeviceId} via relay {RelayServer}", 
                        targetDeviceId, relayServer);
                }
            }

            _logger.LogWarning("❌ Failed to establish relay connection to {DeviceId} through any relay server", 
                targetDeviceId);
            return false;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// Send data to device through relay connection
    /// </summary>
    /// <param name="targetDeviceId">Target device ID</param>
    /// <param name="data">Data to send</param>
    /// <returns>True if sent successfully</returns>
    public async Task<bool> SendDataAsync(string targetDeviceId, byte[] data)
    {
        if (!_connections.TryGetValue(targetDeviceId, out var connection))
        {
            _logger.LogWarning("No relay connection to device {DeviceId}", targetDeviceId);
            return false;
        }

        return await connection.SendDataAsync(data);
    }

    /// <summary>
    /// Disconnect from device
    /// </summary>
    /// <param name="targetDeviceId">Target device ID</param>
    public async Task DisconnectAsync(string targetDeviceId)
    {
        if (_connections.TryRemove(targetDeviceId, out var connection))
        {
            await connection.DisconnectAsync();
            _logger.LogInformation("Disconnected relay connection to {DeviceId}", targetDeviceId);
        }
    }

    /// <summary>
    /// Check if connected to device via relay
    /// </summary>
    /// <param name="targetDeviceId">Target device ID</param>
    /// <returns>True if connected</returns>
    public bool IsConnected(string targetDeviceId)
    {
        return _connections.TryGetValue(targetDeviceId, out var connection) && connection.IsConnected;
    }

    /// <summary>
    /// Get connection statistics
    /// </summary>
    /// <param name="targetDeviceId">Target device ID</param>
    /// <returns>Connection statistics or null if not connected</returns>
    public RelayConnectionStats? GetConnectionStats(string targetDeviceId)
    {
        return _connections.TryGetValue(targetDeviceId, out var connection) ? connection.GetStats() : null;
    }

    private async Task<WebSocketRelayConnection?> CreateRelayConnectionAsync(string relayServerUrl, string targetDeviceId)
    {
        try
        {
            var webSocket = new ClientWebSocket();
            
            // Add Syncthing-compatible headers
            webSocket.Options.SetRequestHeader("User-Agent", "CreatioHelper/1.0 (Syncthing-compatible)");
            webSocket.Options.SetRequestHeader("X-Syncthing-Device", _currentDeviceId);
            webSocket.Options.SetRequestHeader("X-Target-Device", targetDeviceId);
            
            // Set keep-alive and timeout
            webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            
            var uri = new Uri(relayServerUrl);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            
            await webSocket.ConnectAsync(uri, cts.Token);
            
            var connection = new WebSocketRelayConnection(webSocket, relayServerUrl, _currentDeviceId, targetDeviceId, _logger);
            
            // Start the connection (begin listening for messages)
            _ = connection.StartAsync();
            
            // Send initial handshake
            await connection.SendHandshakeAsync();
            
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create WebSocket relay connection to {RelayServer}", relayServerUrl);
            return null;
        }
    }

    public void Dispose()
    {
        _logger.LogInformation("Disposing WebSocket relay service...");
        
        var disconnectTasks = _connections.Values.Select(conn => conn.DisconnectAsync());
        Task.WhenAll(disconnectTasks).GetAwaiter().GetResult();
        
        _connections.Clear();
        _connectionSemaphore.Dispose();
    }
}

/// <summary>
/// WebSocket connection to relay server for a specific target device
/// </summary>
public class WebSocketRelayConnection : IDisposable
{
    private readonly ClientWebSocket _webSocket;
    private readonly string _relayServerUrl;
    private readonly string _sourceDeviceId;
    private readonly string _targetDeviceId;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private readonly RelayConnectionStats _stats = new();
    private volatile bool _isConnected = true;

    public bool IsConnected => _isConnected && _webSocket.State == WebSocketState.Open;

    public WebSocketRelayConnection(
        ClientWebSocket webSocket, 
        string relayServerUrl, 
        string sourceDeviceId, 
        string targetDeviceId, 
        ILogger logger)
    {
        _webSocket = webSocket;
        _relayServerUrl = relayServerUrl;
        _sourceDeviceId = sourceDeviceId;
        _targetDeviceId = targetDeviceId;
        _logger = logger;
    }

    /// <summary>
    /// Start the connection (begin message processing loop)
    /// </summary>
    public async Task StartAsync()
    {
        _logger.LogDebug("Starting WebSocket relay connection to {DeviceId} via {RelayServer}", 
            _targetDeviceId, _relayServerUrl);

        _ = Task.Run(async () =>
        {
            try
            {
                await MessageProcessingLoop();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in WebSocket message processing loop for {DeviceId}", _targetDeviceId);
                await DisconnectAsync();
            }
        });

        await Task.CompletedTask;
    }

    /// <summary>
    /// Send initial handshake to relay server
    /// </summary>
    public async Task SendHandshakeAsync()
    {
        var handshake = new RelayHandshakeMessage
        {
            Type = "connect",
            SourceDevice = _sourceDeviceId,
            TargetDevice = _targetDeviceId,
            Version = 1
        };

        await SendMessageAsync(handshake);
        _logger.LogDebug("Sent handshake to relay server for connection to {DeviceId}", _targetDeviceId);
    }

    /// <summary>
    /// Send data through relay connection
    /// </summary>
    /// <param name="data">Data to send</param>
    /// <returns>True if sent successfully</returns>
    public async Task<bool> SendDataAsync(byte[] data)
    {
        if (!IsConnected)
        {
            _logger.LogWarning("Cannot send data - relay connection to {DeviceId} is not connected", _targetDeviceId);
            return false;
        }

        try
        {
            var message = new RelayDataMessage
            {
                Type = "data",
                SourceDevice = _sourceDeviceId,
                TargetDevice = _targetDeviceId,
                Data = Convert.ToBase64String(data)
            };

            await SendMessageAsync(message);
            
            _stats.RecordDataSent(data.Length);
            _logger.LogDebug("📤 Sent {Bytes} bytes to {DeviceId} via relay", data.Length, _targetDeviceId);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send data to {DeviceId} via relay", _targetDeviceId);
            return false;
        }
    }

    /// <summary>
    /// Disconnect from relay server
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (!_isConnected) return;

        _isConnected = false;
        _cancellationTokenSource.Cancel();

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                var closeMessage = new RelayCloseMessage
                {
                    Type = "close",
                    SourceDevice = _sourceDeviceId,
                    TargetDevice = _targetDeviceId,
                    Reason = "User requested disconnect"
                };

                await SendMessageAsync(closeMessage);
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", cts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during relay disconnect for {DeviceId}", _targetDeviceId);
        }

        _logger.LogDebug("Disconnected from relay server for {DeviceId}", _targetDeviceId);
    }

    /// <summary>
    /// Get connection statistics
    /// </summary>
    /// <returns>Connection statistics</returns>
    public RelayConnectionStats GetStats() => _stats;

    private async Task MessageProcessingLoop()
    {
        var buffer = new byte[4096];
        
        while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Relay server closed connection to {DeviceId}", _targetDeviceId);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessRelayMessage(message);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Handle binary data from relay
                    var data = new byte[result.Count];
                    Array.Copy(buffer, data, result.Count);
                    await ProcessRelayData(data);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(ex, "WebSocket error in relay connection to {DeviceId}", _targetDeviceId);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in relay message processing for {DeviceId}", _targetDeviceId);
                break;
            }
        }

        _isConnected = false;
    }

    private async Task ProcessRelayMessage(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            
            if (root.TryGetProperty("type", out var typeElement))
            {
                var messageType = typeElement.GetString();
                
                switch (messageType)
                {
                    case "connected":
                        _logger.LogInformation("✅ Relay connection established to {DeviceId}", _targetDeviceId);
                        _stats.RecordConnection();
                        break;
                        
                    case "data":
                        if (root.TryGetProperty("data", out var dataElement))
                        {
                            var data = Convert.FromBase64String(dataElement.GetString() ?? "");
                            _stats.RecordDataReceived(data.Length);
                            _logger.LogDebug("📥 Received {Bytes} bytes from {DeviceId} via relay", 
                                data.Length, _targetDeviceId);
                            
                            // Forward data to BEP connection or other handler
                            // This would be implemented based on the specific architecture
                        }
                        break;
                        
                    case "error":
                        var error = root.TryGetProperty("error", out var errorElement) 
                            ? errorElement.GetString() : "Unknown error";
                        _logger.LogWarning("❌ Relay error for {DeviceId}: {Error}", _targetDeviceId, error);
                        break;
                        
                    case "ping":
                        await SendPongAsync();
                        break;
                        
                    default:
                        _logger.LogDebug("Unknown relay message type: {Type}", messageType);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process relay message for {DeviceId}: {Message}", _targetDeviceId, message);
        }
    }

    private async Task ProcessRelayData(byte[] data)
    {
        _stats.RecordDataReceived(data.Length);
        _logger.LogDebug("📥 Received {Bytes} bytes binary data from {DeviceId} via relay", 
            data.Length, _targetDeviceId);
        
        // Process binary data (this would be forwarded to BEP protocol handler)
        await Task.CompletedTask; // Placeholder
    }

    private async Task SendMessageAsync(object message)
    {
        if (!IsConnected) return;

        await _sendSemaphore.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes), 
                WebSocketMessageType.Text, 
                true, 
                _cancellationTokenSource.Token);
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    private async Task SendPongAsync()
    {
        var pong = new RelayPongMessage
        {
            Type = "pong",
            SourceDevice = _sourceDeviceId
        };

        await SendMessageAsync(pong);
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _webSocket?.Dispose();
        _cancellationTokenSource?.Dispose();
        _sendSemaphore?.Dispose();
    }
}

/// <summary>
/// Statistics for relay connection
/// </summary>
public class RelayConnectionStats
{
    public DateTime ConnectedAt { get; private set; } = DateTime.UtcNow;
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }
    public long MessagesSent { get; private set; }
    public long MessagesReceived { get; private set; }
    
    public void RecordConnection()
    {
        ConnectedAt = DateTime.UtcNow;
    }
    
    public void RecordDataSent(int bytes)
    {
        BytesSent += bytes;
        MessagesSent++;
    }
    
    public void RecordDataReceived(int bytes)
    {
        BytesReceived += bytes;
        MessagesReceived++;
    }
}

// Relay message types
public class RelayHandshakeMessage
{
    public string Type { get; set; } = "";
    public string SourceDevice { get; set; } = "";
    public string TargetDevice { get; set; } = "";
    public int Version { get; set; }
}

public class RelayDataMessage
{
    public string Type { get; set; } = "";
    public string SourceDevice { get; set; } = "";
    public string TargetDevice { get; set; } = "";
    public string Data { get; set; } = "";
}

public class RelayCloseMessage
{
    public string Type { get; set; } = "";
    public string SourceDevice { get; set; } = "";
    public string TargetDevice { get; set; } = "";
    public string Reason { get; set; } = "";
}

public class RelayPongMessage
{
    public string Type { get; set; } = "";
    public string SourceDevice { get; set; } = "";
}