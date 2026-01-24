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
    /// Event fired when the relay server reports it is full (RelayFull message)
    /// Following Syncthing pattern from lib/relay/client/static.go
    /// </summary>
    public event EventHandler<RelayFullEventArgs>? RelayFullReceived;

    /// <summary>
    /// Connect to relay server (supports static 'relay://' and dynamic 'dynamic+http(s)://' schemes)
    /// Returns false if connection fails; throws RelayFullException for static relays when full
    /// Following Syncthing pattern from lib/relay/client/client.go
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
        catch (RelayFullException)
        {
            // Re-throw RelayFullException for static relays so callers can try alternative relays
            _logger.LogDebug("Relay {RelayUri} is full", _relayUri);
            await DisconnectAsync();
            throw;
        }
        catch (RelayIncorrectResponseCodeException ex)
        {
            // Log and return false for incorrect response codes
            _logger.LogWarning(ex, "Failed to connect to relay {RelayUri}: incorrect response code", _relayUri);
            await DisconnectAsync();
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Connection to relay {RelayUri} was cancelled", _relayUri);
            await DisconnectAsync();
            return false;
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
    /// Following Syncthing pattern from lib/relay/client/static.go connect() and join()
    /// Throws RelayFullException if the relay is full (to allow callers to try alternatives)
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

        // Verify ALPN negotiation (following Syncthing pattern from lib/relay/client/static.go)
        var negotiatedProtocol = _sslStream.NegotiatedApplicationProtocol;
        var expectedProtocolBytes = System.Text.Encoding.UTF8.GetBytes(RelayProtocol.ProtocolName);

        if (negotiatedProtocol == default ||
            !negotiatedProtocol.Protocol.Span.SequenceEqual(expectedProtocolBytes))
        {
            var actualProtocol = negotiatedProtocol == default
                ? "<none>"
                : System.Text.Encoding.UTF8.GetString(negotiatedProtocol.Protocol.Span);
            _logger.LogError("ALPN protocol negotiation error: expected '{Expected}', got '{Actual}'",
                RelayProtocol.ProtocolName, actualProtocol);
            throw new InvalidOperationException("protocol negotiation error");
        }

        _logger.LogDebug("TLS connection established with ALPN protocol: {Protocol}", RelayProtocol.ProtocolName);

        // Verify relay server identity if ?id= parameter is present (Syncthing pattern)
        await VerifyRelayServerIdentityAsync();

        // Join the relay - may throw RelayFullException
        return await JoinRelayAsync();
    }

    /// <summary>
    /// Connect to dynamic relay server (discovers available relays via HTTP)
    /// Following Syncthing pattern from lib/relay/client/dynamic.go serve()
    /// When a relay is full or disconnects, tries the next relay in the ordered list
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

        // Order relays by latency (Syncthing algorithm from dynamic.go relayAddressesOrder)
        var orderedRelays = await OrderRelaysByLatency(relayUrls);

        // Try connecting to relays in order
        // Following Syncthing pattern: when relay is full or disconnects, try next relay
        foreach (var relayUrl in orderedRelays)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested)
            {
                _logger.LogDebug("Dynamic relay connection cancelled");
                return false;
            }

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
            catch (RelayFullException ex)
            {
                // Relay is full - try the next one (following Syncthing pattern)
                _logger.LogDebug(ex, "Relay {RelayUrl} is full, trying next relay", relayUrl);
                continue;
            }
            catch (RelayIncorrectResponseCodeException ex)
            {
                // Wrong response code (e.g., wrong token) - try the next one
                _logger.LogDebug(ex, "Relay {RelayUrl} returned error code {Code}, trying next relay",
                    relayUrl, ex.Code);
                continue;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Dynamic relay connection cancelled while connecting to {RelayUrl}", relayUrl);
                return false;
            }
            catch (Exception ex)
            {
                // Connection failed - try the next relay
                _logger.LogDebug(ex, "Failed to connect to relay {RelayUrl}, trying next relay", relayUrl);
                continue;
            }
        }

        _logger.LogWarning("Could not find a connectable relay from {Count} dynamic relays", orderedRelays.Count);
        return false;
    }

    /// <summary>
    /// Join the relay server (send JoinRelayRequest and handle response)
    /// Following Syncthing pattern from lib/relay/client/static.go join()
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
                    // Following Syncthing pattern: incorrectResponseCodeErr
                    _logger.LogError("Join relay failed: {Code} - {Message}", joinResponse.Code, joinResponse.Message);
                    throw new RelayIncorrectResponseCodeException(joinResponse.Code, joinResponse.Message);

                case RelayFull:
                    // Following Syncthing pattern: errors.New("relay full")
                    _logger.LogWarning("Relay server is full: {RelayUri}", _relayUri);
                    RelayFullReceived?.Invoke(this, new RelayFullEventArgs(_relayUri));
                    throw new RelayFullException(_relayUri);

                default:
                    _logger.LogError("Expected Response message, got {MessageType}", response.GetType().Name);
                    throw new InvalidOperationException($"protocol error: expecting response got {response.GetType().Name}");
            }
        }
        catch (RelayFullException)
        {
            // Re-throw RelayFullException to allow callers to handle it specifically
            throw;
        }
        catch (RelayIncorrectResponseCodeException)
        {
            // Re-throw to allow callers to handle it specifically
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Join relay cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining relay server");
            throw;
        }
    }

    /// <summary>
    /// Verify relay server identity using the ?id= query parameter (following Syncthing pattern)
    /// See lib/relay/client/static.go performHandshakeAndValidation()
    /// </summary>
    private Task VerifyRelayServerIdentityAsync()
    {
        var query = System.Web.HttpUtility.ParseQueryString(_relayUri.Query);
        var expectedRelayId = query["id"];

        if (string.IsNullOrEmpty(expectedRelayId))
        {
            _logger.LogDebug("No relay ID verification required (no ?id= parameter)");
            return Task.CompletedTask;
        }

        // Get peer certificates from the TLS connection
        var peerCertificate = _sslStream?.RemoteCertificate;
        if (peerCertificate == null)
        {
            throw new InvalidOperationException("Relay ID verification failed: no peer certificate");
        }

        // Syncthing expects exactly one certificate
        using var x509Cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(peerCertificate);

        // Compute device ID from certificate (SHA-256 hash of raw certificate data)
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(x509Cert.RawData);
        var actualRelayId = FormatDeviceIdWithLuhn(hash);

        // Compare device IDs (case-insensitive, ignore hyphens)
        var normalizedExpected = expectedRelayId.Replace("-", "").ToUpperInvariant();
        var normalizedActual = actualRelayId.Replace("-", "").ToUpperInvariant();

        if (!string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
        {
            _logger.LogError("Relay ID verification failed: expected {Expected}, got {Actual}",
                expectedRelayId, actualRelayId);
            throw new InvalidOperationException($"relay id does not match. Expected {expectedRelayId} got {actualRelayId}");
        }

        _logger.LogDebug("Relay server identity verified: {RelayId}", actualRelayId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Format raw SHA-256 hash as Syncthing Device ID with Luhn checksums
    /// Format: AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH
    /// </summary>
    private static string FormatDeviceIdWithLuhn(byte[] hash)
    {
        // Convert to base32 (Syncthing uses RFC 4648 base32 without padding)
        var base32 = ConvertToBase32(hash);

        // Take first 52 characters
        var deviceIdBase = base32.Substring(0, 52);

        // Luhnify: split into 4 groups of 13 chars, add check digit after each
        var luhnified = Luhnify(deviceIdBase);

        // Chunkify: format with hyphens every 7 characters
        return Chunkify(luhnified);
    }

    private static string ConvertToBase32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var result = new System.Text.StringBuilder();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                result.Append(alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            result.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Add Luhn check digits to a 52-character base32 string.
    /// Splits into 4 groups of 13 chars and adds a check digit after each.
    /// Result is 56 characters.
    /// </summary>
    private static string Luhnify(string s)
    {
        if (s.Length != 52)
            throw new ArgumentException($"Input must be 52 characters, got {s.Length}");

        var result = new System.Text.StringBuilder(56);
        for (int i = 0; i < 4; i++)
        {
            var group = s.Substring(i * 13, 13);
            result.Append(group);
            result.Append(CalculateLuhn32(group));
        }
        return result.ToString();
    }

    /// <summary>
    /// Format a 56-character string with hyphens every 7 characters.
    /// </summary>
    private static string Chunkify(string s)
    {
        if (s.Length != 56)
            return s;

        var chunks = new System.Text.StringBuilder(63);
        for (int i = 0; i < 8; i++)
        {
            if (i > 0)
                chunks.Append('-');
            chunks.Append(s.Substring(i * 7, 7));
        }
        return chunks.ToString();
    }

    /// <summary>
    /// Calculate Luhn32 check digit for a base32 string.
    /// This follows the exact Syncthing algorithm from lib/protocol/luhn.go
    /// </summary>
    private static char CalculateLuhn32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        const int n = 32;

        var factor = 1;
        var sum = 0;

        foreach (var c in s)
        {
            var codepoint = Codepoint32(c);
            if (codepoint == -1)
                throw new ArgumentException($"Character '{c}' not valid in base32 alphabet");

            var addend = factor * codepoint;
            factor = (factor == 2) ? 1 : 2;
            addend = (addend / n) + (addend % n);
            sum += addend;
        }

        var remainder = sum % n;
        var checkCodepoint = (n - remainder) % n;
        return alphabet[checkCodepoint];
    }

    private static int Codepoint32(char b)
    {
        if (b >= 'A' && b <= 'Z')
            return b - 'A';
        if (b >= '2' && b <= '7')
            return b - '2' + 26;
        return -1;
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
    /// Following Syncthing pattern from lib/relay/client/static.go serve()
    /// </summary>
    private async Task MessageLoop()
    {
        try
        {
            using var messageTimer = new Timer(OnMessageTimeout, null, _messageTimeout, Timeout.InfiniteTimeSpan);

            while (IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var message = await RelayMessageSerializer.ReadMessageAsync(_sslStream!, _cancellationTokenSource.Token);

                // Reset timeout timer (following Syncthing timeout.Reset pattern)
                messageTimer.Change(_messageTimeout, Timeout.InfiniteTimeSpan);

                await HandleMessageAsync(message);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Message loop cancelled");
        }
        catch (RelayFullException ex)
        {
            // RelayFull is handled by HandleMessageAsync, just log and exit
            _logger.LogDebug(ex, "Message loop exiting due to relay full");
            _isConnected = false;
        }
        catch (Exception ex)
        {
            // Following Syncthing pattern: log disconnect reason
            _logger.LogDebug(ex, "Disconnected from relay {RelayUri} due to error", _relayUri);
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
    /// Following Syncthing pattern from lib/relay/client/static.go serve() message handling
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
                // Following Syncthing pattern: disconnect and return error when relay becomes full
                // From static.go: "Disconnected from relay %s due to it becoming full."
                _logger.LogWarning("Disconnected from relay {RelayUri} due to it becoming full", _relayUri);
                _isConnected = false;
                RelayFullReceived?.Invoke(this, new RelayFullEventArgs(_relayUri));
                throw new RelayFullException(_relayUri);

            case Response response:
                _logger.LogDebug("Received response: {Code} - {Message}", response.Code, response.Message);
                break;

            default:
                // Following Syncthing pattern: return error for unexpected messages
                // From static.go: "protocol error: unexpected message %v"
                _logger.LogWarning("Received unexpected message type: {MessageType}", message.Type);
                throw new InvalidOperationException($"protocol error: unexpected message {message.Type}");
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