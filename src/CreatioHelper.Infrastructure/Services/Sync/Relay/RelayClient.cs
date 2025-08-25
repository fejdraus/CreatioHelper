using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Relay client for connecting to Syncthing-compatible relay servers
/// Implements the relay protocol for establishing connections between devices
/// </summary>
public class RelayClient : IDisposable
{
    private readonly ILogger<RelayClient> _logger;
    private readonly Uri _relayUri;
    private readonly X509Certificate2 _clientCertificate;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _cancellationTokenSource;
    
    private SslStream? _sslStream;
    private NetworkStream? _networkStream;
    private TcpClient? _tcpClient;
    private volatile bool _isConnected;
    private volatile bool _disposed;
    
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SessionInvitation>> _pendingConnections = new();
    private readonly ConcurrentQueue<SessionInvitation> _incomingInvitations = new();
    
    private Task? _messageLoopTask;

    public RelayClient(ILogger<RelayClient> logger, Uri relayUri, X509Certificate2 clientCertificate, TimeSpan timeout)
    {
        _logger = logger;
        _relayUri = relayUri;
        _clientCertificate = clientCertificate;
        _timeout = timeout;
        _cancellationTokenSource = new CancellationTokenSource();
    }

    /// <summary>
    /// Gets whether the client is connected to the relay
    /// </summary>
    public bool IsConnected => _isConnected && !_disposed;

    /// <summary>
    /// Event fired when a session invitation is received
    /// </summary>
    public event EventHandler<SessionInvitation>? SessionInvitationReceived;

    /// <summary>
    /// Connect to the relay server
    /// </summary>
    public async Task<bool> ConnectAsync(string? token = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RelayClient));

        try
        {
            _logger.LogInformation("Connecting to relay server: {RelayUri}", _relayUri);

            // Create TCP connection
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_relayUri.Host, _relayUri.Port);
            _networkStream = _tcpClient.GetStream();

            // Establish TLS connection
            _sslStream = new SslStream(_networkStream, false, ValidateServerCertificate);
            var clientCertificates = new X509CertificateCollection { _clientCertificate };
            
            await _sslStream.AuthenticateAsClientAsync(
                _relayUri.Host,
                clientCertificates,
                System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                false);

            _logger.LogDebug("TLS connection established with relay server");

            // Send JoinRelayRequest
            var joinRequest = new JoinRelayRequest(token ?? "");
            await RelayMessageSerializer.WriteMessageAsync(_sslStream, joinRequest, _cancellationTokenSource.Token);

            // Wait for response
            var response = await RelayMessageSerializer.ReadMessageAsync(_sslStream, _cancellationTokenSource.Token);
            if (response is not Response joinResponse)
            {
                _logger.LogError("Expected Response message, got {MessageType}", response.GetType().Name);
                return false;
            }

            if (joinResponse.Code != 0)
            {
                _logger.LogError("Join relay failed: {Code} - {Message}", joinResponse.Code, joinResponse.Message);
                return false;
            }

            _isConnected = true;
            _logger.LogInformation("Successfully connected to relay server");

            // Start message processing loop
            _messageLoopTask = Task.Run(MessageLoop);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to relay server: {RelayUri}", _relayUri);
            await DisconnectAsync();
            return false;
        }
    }

    /// <summary>
    /// Request a connection to a specific device through the relay
    /// </summary>
    public async Task<SessionInvitation?> RequestConnectionAsync(byte[] deviceId, TimeSpan timeout)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to relay server");

        var deviceIdHex = Convert.ToHexString(deviceId);
        var tcs = new TaskCompletionSource<SessionInvitation>();
        _pendingConnections[deviceIdHex] = tcs;

        try
        {
            _logger.LogDebug("Requesting connection to device {DeviceId} via relay", deviceIdHex);

            var connectRequest = new ConnectRequest(deviceId);
            await RelayMessageSerializer.WriteMessageAsync(_sslStream!, connectRequest, _cancellationTokenSource.Token);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token);

            try
            {
                return await tcs.Task.WaitAsync(combinedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("Connection request to device {DeviceId} timed out", deviceIdHex);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting connection to device {DeviceId}", deviceIdHex);
            return null;
        }
        finally
        {
            _pendingConnections.TryRemove(deviceIdHex, out _);
        }
    }

    /// <summary>
    /// Get the next available session invitation
    /// </summary>
    public bool TryGetSessionInvitation(out SessionInvitation? invitation)
    {
        return _incomingInvitations.TryDequeue(out invitation);
    }

    /// <summary>
    /// Join a session using the provided invitation
    /// </summary>
    public async Task<Stream?> JoinSessionAsync(SessionInvitation invitation)
    {
        try
        {
            _logger.LogDebug("Joining session: {Invitation}", invitation);

            // Create connection to the session address
            var tcpClient = new TcpClient();
            var ipAddress = new System.Net.IPAddress(invitation.Address);
            await tcpClient.ConnectAsync(ipAddress, invitation.Port);
            
            var stream = tcpClient.GetStream();

            // Send session key
            var joinSessionRequest = new JoinSessionRequest(invitation.Key);
            await RelayMessageSerializer.WriteMessageAsync(stream, joinSessionRequest, _cancellationTokenSource.Token);

            // Wait for success response
            var response = await RelayMessageSerializer.ReadMessageAsync(stream, _cancellationTokenSource.Token);
            if (response is Response joinResponse && joinResponse.Code == 0)
            {
                _logger.LogDebug("Successfully joined session");
                return stream;
            }
            else
            {
                _logger.LogError("Failed to join session: {Response}", response);
                stream.Dispose();
                tcpClient.Dispose();
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining session: {Invitation}", invitation);
            return null;
        }
    }

    /// <summary>
    /// Send a ping message to keep the connection alive
    /// </summary>
    public async Task<bool> PingAsync()
    {
        if (!IsConnected)
            return false;

        try
        {
            var ping = new Ping();
            await RelayMessageSerializer.WriteMessageAsync(_sslStream!, ping, _cancellationTokenSource.Token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ping to relay server");
            return false;
        }
    }

    private async Task MessageLoop()
    {
        try
        {
            while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var message = await RelayMessageSerializer.ReadMessageAsync(_sslStream!, _cancellationTokenSource.Token);
                await HandleMessageAsync(message);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Message loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in relay message loop");
            _isConnected = false;
        }
    }

    private async Task HandleMessageAsync(IRelayMessage message)
    {
        _logger.LogTrace("Received relay message: {MessageType}", message.Type);

        switch (message)
        {
            case SessionInvitation invitation:
                HandleSessionInvitation(invitation);
                break;

            case Response response:
                await HandleResponse(response);
                break;

            case Ping:
                // Respond to ping with pong
                await RelayMessageSerializer.WriteMessageAsync(_sslStream!, new Pong(), _cancellationTokenSource.Token);
                break;

            case Pong:
                // Pong received, connection is alive
                _logger.LogTrace("Received pong from relay server");
                break;

            case RelayFull:
                _logger.LogWarning("Relay server is full");
                break;

            default:
                _logger.LogWarning("Received unexpected message type: {MessageType}", message.Type);
                break;
        }
    }

    private void HandleSessionInvitation(SessionInvitation invitation)
    {
        _logger.LogDebug("Received session invitation: {Invitation}", invitation);
        
        var deviceIdHex = Convert.ToHexString(invitation.From);
        
        // Check if this is a response to a pending connection request
        if (_pendingConnections.TryRemove(deviceIdHex, out var tcs))
        {
            tcs.SetResult(invitation);
        }
        else
        {
            // This is an incoming connection invitation
            _incomingInvitations.Enqueue(invitation);
            SessionInvitationReceived?.Invoke(this, invitation);
        }
    }

    private Task HandleResponse(Response response)
    {
        if (response.Code != 0)
        {
            _logger.LogWarning("Received error response from relay: {Code} - {Message}", response.Code, response.Message);
        }
        else
        {
            _logger.LogTrace("Received success response from relay");
        }
        return Task.CompletedTask;
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        // For relay connections, we typically accept any certificate as relays are public infrastructure
        // In production, you might want to implement proper certificate validation
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        _logger.LogWarning("SSL certificate validation issues: {SslPolicyErrors}", sslPolicyErrors);
        
        // Allow self-signed certificates for relay servers
        if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Disconnect from the relay server
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_disposed)
            return;

        _isConnected = false;
        _cancellationTokenSource.Cancel();

        try
        {
            if (_messageLoopTask != null)
            {
                await _messageLoopTask;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for message loop to complete");
        }

        _sslStream?.Dispose();
        _networkStream?.Dispose();
        _tcpClient?.Dispose();

        _sslStream = null;
        _networkStream = null;
        _tcpClient = null;

        _logger.LogInformation("Disconnected from relay server");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        
        try
        {
            DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during disposal");
        }

        _cancellationTokenSource.Dispose();
    }
}