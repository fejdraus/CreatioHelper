using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// 100% Syncthing-compatible relay client with static and dynamic relay support
/// Implements complete Syncthing relay protocol v1 with ALPN negotiation
/// </summary>
public class SyncthingRelayClient : IDisposable
{
    private readonly ILogger<SyncthingRelayClient> _logger;
    private readonly Uri _relayUri;
    private readonly X509Certificate2Collection _certificates;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _messageTimeout;
    private readonly CancellationTokenSource _cancellationTokenSource;

    private SslStream? _sslStream;
    private NetworkStream? _networkStream;
    private TcpClient? _tcpClient;
    private volatile bool _isConnected;
    private volatile bool _disposed;

    private readonly Dictionary<string, TaskCompletionSource<SessionInvitation>> _pendingConnections = new();
    private readonly Queue<SessionInvitation> _incomingInvitations = new();
    private readonly object _lock = new object();

    private Task? _messageLoopTask;
    private readonly string? _token;

    public SyncthingRelayClient(
        ILogger<SyncthingRelayClient> logger,
        Uri relayUri,
        X509Certificate2Collection certificates,
        TimeSpan connectTimeout = default,
        TimeSpan messageTimeout = default)
    {
        _logger = logger;
        _relayUri = relayUri;
        _certificates = certificates;
        _connectTimeout = connectTimeout == default ? TimeSpan.FromSeconds(10) : connectTimeout;
        _messageTimeout = messageTimeout == default ? TimeSpan.FromMinutes(2) : messageTimeout;
        _cancellationTokenSource = new CancellationTokenSource();

        // Extract token from URI query parameters (Syncthing style)
        var query = System.Web.HttpUtility.ParseQueryString(_relayUri.Query);
        _token = query["token"];
    }

    /// <summary>
    /// Gets whether the client is connected to the relay
    /// </summary>
    public bool IsConnected => _isConnected && !_disposed;

    /// <summary>
    /// Gets the relay URI
    /// </summary>
    public Uri URI => _relayUri;

    /// <summary>
    /// Event fired when a session invitation is received
    /// </summary>
    public event EventHandler<SessionInvitation>? SessionInvitationReceived;

    /// <summary>
    /// Connect to relay server (supports static 'relay://' and dynamic 'dynamic+http(s)://' schemes)
    /// </summary>
    public async Task<bool> ConnectAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SyncthingRelayClient));

        try
        {
            switch (_relayUri.Scheme)
            {
                case "relay":
                    return await ConnectStaticAsync();
                case "dynamic+http":
                case "dynamic+https":
                    return await ConnectDynamicAsync();
                default:
                    throw new NotSupportedException($"Unsupported relay scheme: {_relayUri.Scheme}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to relay: {RelayUri}", _relayUri);
            await DisconnectAsync();
            return false;
        }
    }

    /// <summary>
    /// Connect to static relay server
    /// </summary>
    private async Task<bool> ConnectStaticAsync()
    {
        _logger.LogInformation("Connecting to static relay server: {RelayUri}", _relayUri);

        // Create TCP connection
        _tcpClient = new TcpClient();
        using var connectTimeoutCts = new CancellationTokenSource(_connectTimeout);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, connectTimeoutCts.Token);

        await _tcpClient.ConnectAsync(_relayUri.Host, _relayUri.Port, combinedCts.Token);
        _networkStream = _tcpClient.GetStream();

        // Establish TLS connection with ALPN negotiation (Syncthing protocol)
        _sslStream = new SslStream(_networkStream, false, ValidateServerCertificate);

        var clientAuthOptions = new SslClientAuthenticationOptions
        {
            TargetHost = _relayUri.Host,
            ClientCertificates = _certificates,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = new List<SslApplicationProtocol> 
            { 
                new SslApplicationProtocol(RelayProtocol.ProtocolName) 
            }
        };

        await _sslStream.AuthenticateAsClientAsync(clientAuthOptions, combinedCts.Token);

        // Verify ALPN negotiation
        if (_sslStream.NegotiatedApplicationProtocol.Protocol.ToArray() != 
            System.Text.Encoding.UTF8.GetBytes(RelayProtocol.ProtocolName))
        {
            throw new InvalidOperationException("ALPN protocol negotiation failed");
        }

        _logger.LogDebug("TLS connection established with ALPN protocol: {Protocol}", RelayProtocol.ProtocolName);

        // Join the relay
        return await JoinRelayAsync();
    }

    /// <summary>
    /// Connect to dynamic relay server (discovers available relays via HTTP)
    /// </summary>
    private async Task<bool> ConnectDynamicAsync()
    {
        _logger.LogInformation("Discovering dynamic relays from: {PoolAddress}", _relayUri);

        // Convert dynamic+http(s):// to http(s)://
        var discoveryUri = new UriBuilder(_relayUri)
        {
            Scheme = _relayUri.Scheme.Substring(8) // Remove "dynamic+" prefix
        }.Uri;

        using var httpClient = new HttpClient { Timeout = _connectTimeout };
        using var response = await httpClient.GetAsync(discoveryUri, _cancellationTokenSource.Token);
        response.EnsureSuccessStatusCode();

        var announcement = await response.Content.ReadFromJsonAsync<DynamicAnnouncement>(_cancellationTokenSource.Token);
        if (announcement?.Relays == null || announcement.Relays.Length == 0)
        {
            _logger.LogError("No relays found in dynamic announcement");
            return false;
        }

        var relayUrls = announcement.Relays.Select(r => r.Url).ToList();
        _logger.LogDebug("Found {Count} dynamic relays", relayUrls.Count);

        // Order relays by latency (Syncthing algorithm)
        var orderedRelays = await OrderRelaysByLatency(relayUrls);

        // Try connecting to relays in order
        foreach (var relayUrl in orderedRelays)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested)
                return false;

            try
            {
                var relayUri = new Uri(relayUrl);
                var staticClient = new SyncthingRelayClient(_logger, relayUri, _certificates, _connectTimeout, _messageTimeout);
                
                if (await staticClient.ConnectStaticAsync())
                {
                    // Transfer connection to this client
                    _tcpClient = staticClient._tcpClient;
                    _networkStream = staticClient._networkStream;
                    _sslStream = staticClient._sslStream;
                    _isConnected = staticClient._isConnected;

                    // Prevent disposal in static client
                    staticClient._tcpClient = null;
                    staticClient._networkStream = null;
                    staticClient._sslStream = null;
                    staticClient._isConnected = false;
                    staticClient.Dispose();

                    _logger.LogInformation("Successfully connected to dynamic relay: {RelayUrl}", relayUrl);
                    _messageLoopTask = Task.Run(MessageLoop);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to relay {RelayUrl}", relayUrl);
                continue;
            }
        }

        _logger.LogError("Could not connect to any dynamic relay");
        return false;
    }

    /// <summary>
    /// Join the relay server (send JoinRelayRequest and handle response)
    /// </summary>
    private async Task<bool> JoinRelayAsync()
    {
        try
        {
            // Send JoinRelayRequest
            var joinRequest = new JoinRelayRequest(_token ?? "");
            await RelayMessageSerializer.WriteMessageAsync(_sslStream!, joinRequest, _cancellationTokenSource.Token);

            // Wait for response with timeout
            using var timeoutCts = new CancellationTokenSource(_connectTimeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token);

            var response = await RelayMessageSerializer.ReadMessageAsync(_sslStream!, combinedCts.Token);

            switch (response)
            {
                case Response joinResponse when joinResponse.Code == 0:
                    _isConnected = true;
                    _logger.LogInformation("Successfully joined relay server");
                    _messageLoopTask = Task.Run(MessageLoop);
                    return true;

                case Response joinResponse:
                    _logger.LogError("Join relay failed: {Code} - {Message}", joinResponse.Code, joinResponse.Message);
                    return false;

                case RelayFull:
                    _logger.LogError("Relay server is full");
                    return false;

                default:
                    _logger.LogError("Expected Response message, got {MessageType}", response.GetType().Name);
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining relay server");
            return false;
        }
    }

    /// <summary>
    /// Order relay addresses by latency (Syncthing algorithm with 50ms buckets)
    /// </summary>
    private async Task<List<string>> OrderRelaysByLatency(List<string> relayUrls)
    {
        var buckets = new Dictionary<int, List<string>>();

        foreach (var relayUrl in relayUrls)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested)
                break;

            try
            {
                var latency = await MeasureLatency(relayUrl);
                var bucketId = (int)(latency.TotalMilliseconds / 50); // 50ms buckets

                if (!buckets.ContainsKey(bucketId))
                    buckets[bucketId] = new List<string>();

                buckets[bucketId].Add(relayUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not measure latency for relay {RelayUrl}", relayUrl);
                // Put in highest latency bucket
                if (!buckets.ContainsKey(int.MaxValue))
                    buckets[int.MaxValue] = new List<string>();
                buckets[int.MaxValue].Add(relayUrl);
            }
        }

        // Shuffle each bucket and combine in order
        var random = new Random();
        var orderedRelays = new List<string>();

        foreach (var bucketId in buckets.Keys.OrderBy(k => k))
        {
            var bucket = buckets[bucketId];
            for (int i = bucket.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (bucket[i], bucket[j]) = (bucket[j], bucket[i]);
            }
            orderedRelays.AddRange(bucket);
        }

        return orderedRelays;
    }

    /// <summary>
    /// Measure latency to a relay URL
    /// </summary>
    private async Task<TimeSpan> MeasureLatency(string relayUrl)
    {
        try
        {
            var uri = new Uri(relayUrl);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(uri.Host, uri.Port);
            tcpClient.Close();

            stopwatch.Stop();
            return stopwatch.Elapsed;
        }
        catch
        {
            return TimeSpan.FromHours(1); // Max latency for failed connections
        }
    }

    /// <summary>
    /// Main message processing loop with timeout handling
    /// </summary>
    private async Task MessageLoop()
    {
        try
        {
            using var messageTimer = new Timer(OnMessageTimeout, null, _messageTimeout, Timeout.InfiniteTimeSpan);

            while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var message = await RelayMessageSerializer.ReadMessageAsync(_sslStream!, _cancellationTokenSource.Token);
                
                // Reset timeout timer
                messageTimer.Change(_messageTimeout, Timeout.InfiniteTimeSpan);

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

    private void OnMessageTimeout(object? state)
    {
        _logger.LogWarning("Message timeout reached, disconnecting from relay");
        _isConnected = false;
        _cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// Handle incoming relay messages
    /// </summary>
    private async Task HandleMessageAsync(IRelayMessage message)
    {
        _logger.LogTrace("Received relay message: {MessageType}", message.Type);

        switch (message)
        {
            case SessionInvitation invitation:
                HandleSessionInvitation(invitation);
                break;

            case Ping:
                await RelayMessageSerializer.WriteMessageAsync(_sslStream!, new Pong(), _cancellationTokenSource.Token);
                _logger.LogTrace("Responded to ping with pong");
                break;

            case Pong:
                _logger.LogTrace("Received pong from relay server");
                break;

            case RelayFull:
                _logger.LogWarning("Relay server is full - disconnecting");
                _isConnected = false;
                break;

            case Response response:
                _logger.LogDebug("Received response: {Code} - {Message}", response.Code, response.Message);
                break;

            default:
                _logger.LogWarning("Received unexpected message type: {MessageType}", message.Type);
                break;
        }
    }

    /// <summary>
    /// Handle session invitation
    /// </summary>
    private void HandleSessionInvitation(SessionInvitation invitation)
    {
        _logger.LogDebug("Received session invitation: {Invitation}", invitation);

        lock (_lock)
        {
            var deviceIdHex = Convert.ToHexString(invitation.From);

            // Check if this is a response to a pending connection request
            if (_pendingConnections.TryGetValue(deviceIdHex, out var tcs))
            {
                _pendingConnections.Remove(deviceIdHex);
                tcs.SetResult(invitation);
            }
            else
            {
                // This is an incoming connection invitation
                _incomingInvitations.Enqueue(invitation);
                SessionInvitationReceived?.Invoke(this, invitation);
            }
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

        lock (_lock)
        {
            _pendingConnections[deviceIdHex] = tcs;
        }

        try
        {
            _logger.LogDebug("Requesting connection to device {DeviceId} via relay", deviceIdHex);

            var connectRequest = new ConnectRequest(deviceId);
            await RelayMessageSerializer.WriteMessageAsync(_sslStream!, connectRequest, _cancellationTokenSource.Token);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token);

            return await tcs.Task.WaitAsync(combinedCts.Token);
        }
        catch (OperationCanceledException) when (timeout != Timeout.InfiniteTimeSpan)
        {
            _logger.LogWarning("Connection request to device {DeviceId} timed out", deviceIdHex);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting connection to device {DeviceId}", deviceIdHex);
            return null;
        }
        finally
        {
            lock (_lock)
            {
                _pendingConnections.Remove(deviceIdHex);
            }
        }
    }

    /// <summary>
    /// Get the next available session invitation
    /// </summary>
    public SessionInvitation? GetNextInvitation()
    {
        lock (_lock)
        {
            return _incomingInvitations.Count > 0 ? _incomingInvitations.Dequeue() : null;
        }
    }

    /// <summary>
    /// Send ping to keep connection alive
    /// </summary>
    public async Task<bool> PingAsync()
    {
        if (!IsConnected)
            return false;

        try
        {
            await RelayMessageSerializer.WriteMessageAsync(_sslStream!, new Ping(), _cancellationTokenSource.Token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ping to relay server");
            return false;
        }
    }

    /// <summary>
    /// Validate server certificate (allows self-signed for relay servers)
    /// </summary>
    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        _logger.LogWarning("SSL certificate validation issues: {SslPolicyErrors}", sslPolicyErrors);

        // Allow self-signed certificates for relay servers (common in Syncthing)
        if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
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
        await _cancellationTokenSource.CancelAsync();

        try
        {
            if (_messageLoopTask != null)
            {
                await _messageLoopTask.WaitAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>
    /// Dynamic announcement structure for JSON deserialization
    /// </summary>
    private class DynamicAnnouncement
    {
        public RelayInfo[] Relays { get; set; } = Array.Empty<RelayInfo>();
    }

    private class RelayInfo
    {
        public string Url { get; set; } = "";
    }
}