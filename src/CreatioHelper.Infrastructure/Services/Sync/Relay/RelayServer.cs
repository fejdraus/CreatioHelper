using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Relay server implementation compatible with Syncthing relay protocol
/// Acts as an intermediary for connections between Syncthing devices
/// </summary>
public class RelayServer : IDisposable
{
    private readonly ILogger<RelayServer> _logger;
    private readonly IPEndPoint _listenEndpoint;
    private readonly X509Certificate2 _serverCertificate;
    private readonly string? _accessToken;
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _networkTimeout;
    
    private TcpListener? _tcpListener;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private volatile bool _isRunning;
    private volatile bool _disposed;
    
    private readonly ConcurrentDictionary<string, RelaySession> _activeSessions = new();
    private readonly ConcurrentDictionary<string, RelayConnection> _connections = new();
    
    private Task? _acceptTask;
    private readonly Timer _cleanupTimer;

    public RelayServer(
        ILogger<RelayServer> logger,
        IPEndPoint listenEndpoint,
        X509Certificate2 serverCertificate,
        string? accessToken = null,
        TimeSpan? pingInterval = null,
        TimeSpan? networkTimeout = null)
    {
        _logger = logger;
        _listenEndpoint = listenEndpoint;
        _serverCertificate = serverCertificate;
        _accessToken = accessToken;
        _pingInterval = pingInterval ?? TimeSpan.FromMinutes(1);
        _networkTimeout = networkTimeout ?? TimeSpan.FromMinutes(2);
        _cancellationTokenSource = new CancellationTokenSource();
        
        // Setup periodic cleanup
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Gets the number of active sessions
    /// </summary>
    public int ActiveSessionCount => _activeSessions.Count;

    /// <summary>
    /// Gets the number of connected clients
    /// </summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Start the relay server
    /// </summary>
    public Task StartAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RelayServer));

        if (_isRunning)
            return Task.CompletedTask;

        _logger.LogInformation("Starting relay server on {Endpoint}", _listenEndpoint);

        _tcpListener = new TcpListener(_listenEndpoint);
        _tcpListener.Start();
        
        _isRunning = true;
        _acceptTask = Task.Run(AcceptLoop);
        
        _logger.LogInformation("Relay server started on {Endpoint}", _listenEndpoint);
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the relay server
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _logger.LogInformation("Stopping relay server");

        _isRunning = false;
        _cancellationTokenSource.Cancel();

        _tcpListener?.Stop();

        if (_acceptTask != null)
        {
            await _acceptTask;
        }

        // Close all active sessions
        foreach (var session in _activeSessions.Values)
        {
            session.Dispose();
        }
        _activeSessions.Clear();

        // Close all connections
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }
        _connections.Clear();

        _logger.LogInformation("Relay server stopped");
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var tcpClient = await _tcpListener!.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(tcpClient));
            }
        }
        catch (ObjectDisposedException)
        {
            // Expected when stopping
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in accept loop");
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient)
    {
        var clientEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var connectionId = Guid.NewGuid().ToString();
        
        try
        {
            _logger.LogDebug("New client connection from {ClientEndpoint} [{ConnectionId}]", clientEndpoint, connectionId);

            var networkStream = tcpClient.GetStream();
            var sslStream = new SslStream(networkStream, false);

            // Authenticate as server
            await sslStream.AuthenticateAsServerAsync(_serverCertificate);

            var connection = new RelayConnection(connectionId, tcpClient, sslStream, _logger);
            _connections[connectionId] = connection;

            // Handle the connection
            await HandleConnectionAsync(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ClientEndpoint} [{ConnectionId}]", clientEndpoint, connectionId);
        }
        finally
        {
            _connections.TryRemove(connectionId, out var conn);
            conn?.Dispose();
        }
    }

    private async Task HandleConnectionAsync(RelayConnection connection)
    {
        try
        {
            // Wait for JoinRelayRequest
            var message = await RelayMessageSerializer.ReadMessageAsync(connection.Stream, _cancellationTokenSource.Token);
            
            if (message is not JoinRelayRequest joinRequest)
            {
                await SendResponseAsync(connection, Response.UnexpectedMessage);
                return;
            }

            // Validate access token if required
            if (!string.IsNullOrEmpty(_accessToken))
            {
                if (joinRequest.Token != _accessToken)
                {
                    await SendResponseAsync(connection, Response.WrongToken);
                    _logger.LogWarning("Client {ConnectionId} provided wrong token", connection.Id);
                    return;
                }
            }

            // Device ID извлекается из TLS сертификата клиента через ComputeDeviceId()
            // В relay протоколе используется временный ID на основе connection
            connection.DeviceId = $"relay-client-{connection.Id}";

            // Accept the connection
            await SendResponseAsync(connection, Response.Success);
            _logger.LogDebug("Client {ConnectionId} joined relay", connection.Id);

            // Handle messages from this connection
            await MessageLoop(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in connection handling for {ConnectionId}", connection.Id);
        }
    }

    private async Task MessageLoop(RelayConnection connection)
    {
        var lastActivity = DateTime.UtcNow;
        
        try
        {
            while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                // Check for timeout
                if (DateTime.UtcNow - lastActivity > _networkTimeout)
                {
                    _logger.LogDebug("Connection {ConnectionId} timed out", connection.Id);
                    break;
                }

                using var timeoutCts = new CancellationTokenSource(_networkTimeout);
                try
                {
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token);
                    
                    var message = await RelayMessageSerializer.ReadMessageAsync(connection.Stream, combinedCts.Token);
                    lastActivity = DateTime.UtcNow;
                    
                    await HandleMessageAsync(connection, message);
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    // Timeout occurred, send ping
                    await SendPingAsync(connection);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in message loop for connection {ConnectionId}", connection.Id);
        }
    }

    private async Task HandleMessageAsync(RelayConnection connection, IRelayMessage message)
    {
        _logger.LogTrace("Received message {MessageType} from connection {ConnectionId}", message.Type, connection.Id);

        switch (message)
        {
            case ConnectRequest connectRequest:
                await HandleConnectRequest(connection, connectRequest);
                break;

            case Ping:
                await SendPongAsync(connection);
                break;

            case Pong:
                // Keep-alive response
                _logger.LogTrace("Received pong from connection {ConnectionId}", connection.Id);
                break;

            default:
                _logger.LogWarning("Unexpected message type {MessageType} from connection {ConnectionId}", message.Type, connection.Id);
                await SendResponseAsync(connection, Response.UnexpectedMessage);
                break;
        }
    }

    private async Task HandleConnectRequest(RelayConnection requestingConnection, ConnectRequest request)
    {
        var targetDeviceId = Convert.ToHexString(request.Id);
        _logger.LogDebug("Connection request from {ConnectionId} to device {TargetDeviceId}", requestingConnection.Id, targetDeviceId);

        // Find target device connection
        var targetConnection = _connections.Values.FirstOrDefault(c => 
            c.DeviceId != null && c.DeviceId.Equals(targetDeviceId, StringComparison.OrdinalIgnoreCase));

        if (targetConnection == null)
        {
            await SendResponseAsync(requestingConnection, Response.NotFound);
            _logger.LogDebug("Target device {TargetDeviceId} not found for connection request from {ConnectionId}", 
                targetDeviceId, requestingConnection.Id);
            return;
        }

        // Create session
        var sessionKey = GenerateSessionKey();
        var sessionId = Guid.NewGuid().ToString();
        
        var session = new RelaySession(
            sessionId,
            requestingConnection.Id,
            targetConnection.Id,
            sessionKey,
            DateTime.UtcNow.Add(_networkTimeout)
        );

        _activeSessions[sessionId] = session;

        // Send invitations to both clients
        var listenAddress = ((IPEndPoint)_tcpListener!.LocalEndpoint).Address.GetAddressBytes();
        var listenPort = (ushort)((IPEndPoint)_tcpListener.LocalEndpoint).Port;

        var requestingInvitation = new SessionInvitation(
            request.Id,
            sessionKey,
            listenAddress,
            listenPort,
            true  // Server socket
        );

        var targetInvitation = new SessionInvitation(
            request.Id, // From requesting device
            sessionKey,
            listenAddress,
            listenPort,
            false // Client socket
        );

        await RelayMessageSerializer.WriteMessageAsync(requestingConnection.Stream, requestingInvitation, _cancellationTokenSource.Token);
        await RelayMessageSerializer.WriteMessageAsync(targetConnection.Stream, targetInvitation, _cancellationTokenSource.Token);

        _logger.LogDebug("Created session {SessionId} between {RequestingConnection} and {TargetConnection}",
            sessionId, requestingConnection.Id, targetConnection.Id);
    }

    private async Task SendResponseAsync(RelayConnection connection, Response response)
    {
        try
        {
            await RelayMessageSerializer.WriteMessageAsync(connection.Stream, response, _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send response to connection {ConnectionId}", connection.Id);
        }
    }

    private async Task SendPingAsync(RelayConnection connection)
    {
        try
        {
            await RelayMessageSerializer.WriteMessageAsync(connection.Stream, new Ping(), _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ping to connection {ConnectionId}", connection.Id);
        }
    }

    private async Task SendPongAsync(RelayConnection connection)
    {
        try
        {
            await RelayMessageSerializer.WriteMessageAsync(connection.Stream, new Pong(), _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send pong to connection {ConnectionId}", connection.Id);
        }
    }

    private static byte[] GenerateSessionKey()
    {
        var key = new byte[32];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(key);
        }
        return key;
    }

    private void CleanupExpiredSessions(object? state)
    {
        var now = DateTime.UtcNow;
        var expiredSessions = _activeSessions.Where(kvp => kvp.Value.ExpiresAt < now).ToList();

        foreach (var (sessionId, session) in expiredSessions)
        {
            if (_activeSessions.TryRemove(sessionId, out _))
            {
                session.Dispose();
                _logger.LogDebug("Cleaned up expired session {SessionId}", sessionId);
            }
        }

        if (expiredSessions.Count > 0)
        {
            _logger.LogDebug("Cleaned up {ExpiredCount} expired sessions", expiredSessions.Count);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during disposal");
        }

        _cleanupTimer.Dispose();
        try { _cancellationTokenSource.Dispose(); } catch (ObjectDisposedException) { }
    }
}

/// <summary>
/// Represents a connection to the relay server
/// </summary>
internal class RelayConnection : IDisposable
{
    public string Id { get; }
    public TcpClient TcpClient { get; }
    public SslStream Stream { get; }
    public string? DeviceId { get; set; }
    public DateTime ConnectedAt { get; }
    
    private readonly ILogger _logger;

    public RelayConnection(string id, TcpClient tcpClient, SslStream stream, ILogger logger)
    {
        Id = id;
        TcpClient = tcpClient;
        Stream = stream;
        ConnectedAt = DateTime.UtcNow;
        _logger = logger;
    }

    public void Dispose()
    {
        try
        {
            Stream?.Dispose();
            TcpClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing connection {ConnectionId}", Id);
        }
    }
}

/// <summary>
/// Represents an active relay session between two devices
/// </summary>
internal class RelaySession : IDisposable
{
    public string SessionId { get; }
    public string RequestingConnectionId { get; }
    public string TargetConnectionId { get; }
    public byte[] SessionKey { get; }
    public DateTime ExpiresAt { get; }

    public RelaySession(string sessionId, string requestingConnectionId, string targetConnectionId, 
                       byte[] sessionKey, DateTime expiresAt)
    {
        SessionId = sessionId;
        RequestingConnectionId = requestingConnectionId;
        TargetConnectionId = targetConnectionId;
        SessionKey = sessionKey;
        ExpiresAt = expiresAt;
    }

    public void Dispose()
    {
        // Cleanup resources if needed
    }
}