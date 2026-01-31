using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// Global device discovery using Syncthing discovery servers.
/// Implements the Syncthing global discovery protocol (v3).
///
/// Protocol verification against Syncthing (lib/discover/global.go):
/// - Announce: POST to server URL with JSON body {"addresses": [...]}
/// - Device identity is provided via TLS client certificate (not URL parameter)
/// - Lookup: GET to server URL with ?device={deviceId} query parameter
/// - Response headers: Reannounce-After (success), Retry-After (errors)
/// - HTTP/2 enabled for performance
/// - TLS 1.2+ required
/// </summary>
public class GlobalDiscovery : IAsyncDisposable
{
    private readonly ILogger<GlobalDiscovery> _logger;
    private readonly HttpClient _announceClient;
    private readonly HttpClient _queryClient;
    private readonly string _deviceId;
    private readonly List<string> _discoveryServers;
    private readonly int _lookupCacheSeconds;
    private readonly X509Certificate2? _clientCertificate;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _announceTask;
    private readonly Dictionary<string, CachedLookup> _lookupCache = new();
    private readonly object _cacheLock = new();

    // Announce interval can be adjusted by server via Reannounce-After header
    private int _announceIntervalSeconds;

    // Per Syncthing: noLookup option disables lookups (announce-only mode)
    private readonly bool _noLookup;

    // Error tracking for status reporting
    private string? _lastError;
    private readonly object _errorLock = new();

    /// <summary>
    /// Default reannounce interval (30 minutes) per Syncthing protocol.
    /// </summary>
    public const int DefaultReannounceIntervalSeconds = 1800; // 30 minutes

    /// <summary>
    /// Error retry interval (5 minutes) per Syncthing protocol.
    /// </summary>
    public const int ErrorRetryIntervalSeconds = 300; // 5 minutes

    /// <summary>
    /// Request timeout (30 seconds) per Syncthing protocol.
    /// </summary>
    public const int RequestTimeoutSeconds = 30;

    // No hardcoded discovery servers — must be configured explicitly
    private static readonly string[] DefaultDiscoveryServers = Array.Empty<string>();

    /// <summary>
    /// Event fired when a device is discovered via global discovery.
    /// </summary>
    public event Func<DiscoveredDevice, Task>? DeviceDiscovered;

    /// <summary>
    /// Creates a new GlobalDiscovery instance.
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="deviceId">Our device ID (used for logging; actual auth via TLS cert)</param>
    /// <param name="clientCertificate">TLS client certificate for authentication with discovery servers</param>
    /// <param name="discoveryServers">Discovery server URLs (defaults to Syncthing servers)</param>
    /// <param name="announceIntervalSeconds">Initial announce interval (server may override via Reannounce-After)</param>
    /// <param name="lookupCacheSeconds">Lookup result cache duration</param>
    /// <param name="insecureSkipVerify">Skip server certificate verification (for testing only)</param>
    /// <param name="noLookup">Disable lookups (announce-only mode per Syncthing ?nolookup option)</param>
    public GlobalDiscovery(
        ILogger<GlobalDiscovery> logger,
        string deviceId,
        X509Certificate2? clientCertificate = null,
        IEnumerable<string>? discoveryServers = null,
        int announceIntervalSeconds = DefaultReannounceIntervalSeconds,
        int lookupCacheSeconds = 300,
        bool insecureSkipVerify = false,
        bool noLookup = false)
    {
        _logger = logger;
        _deviceId = deviceId;
        _clientCertificate = clientCertificate;
        _discoveryServers = discoveryServers?.ToList() ?? DefaultDiscoveryServers.ToList();
        _announceIntervalSeconds = announceIntervalSeconds;
        _lookupCacheSeconds = lookupCacheSeconds;
        _noLookup = noLookup;

        // Create the HTTP client for announcements - requires TLS client certificate for auth
        // Per Syncthing global.go: "The http.Client used for announcements. It needs to have our
        // certificate to prove our identity"
        _announceClient = CreateHttpClient(clientCertificate, insecureSkipVerify, disableKeepAlive: true);

        // Create the HTTP client for queries - no client certificate needed
        // Per Syncthing global.go: "The http.Client used for queries. We don't need to present our
        // certificate here"
        _queryClient = CreateHttpClient(certificate: null, insecureSkipVerify, disableKeepAlive: false);
    }

    /// <summary>
    /// Creates a new GlobalDiscovery instance (legacy constructor for backwards compatibility).
    /// </summary>
    [Obsolete("Use constructor with X509Certificate2 parameter for proper TLS authentication")]
    public GlobalDiscovery(
        ILogger<GlobalDiscovery> logger,
        string deviceId,
        IEnumerable<string>? discoveryServers = null,
        int announceIntervalSeconds = DefaultReannounceIntervalSeconds,
        int lookupCacheSeconds = 300)
        : this(logger, deviceId, null, discoveryServers, announceIntervalSeconds, lookupCacheSeconds, false)
    {
    }

    private static HttpClient CreateHttpClient(
        X509Certificate2? certificate,
        bool insecureSkipVerify,
        bool disableKeepAlive)
    {
        var handler = new SocketsHttpHandler
        {
            // Enable HTTP/2 for performance (per Syncthing global.go http2EnabledTransport)
            EnableMultipleHttp2Connections = true,

            // TLS configuration per Syncthing protocol
            SslOptions = new SslClientAuthenticationOptions
            {
                // TLS 1.2+ required per Syncthing: MinVersion: tls.VersionTLS12
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                     System.Security.Authentication.SslProtocols.Tls13,

                // Client certificate for authentication (announce client only)
                ClientCertificates = certificate != null
                    ? new X509Certificate2Collection { certificate }
                    : null,

                // Server certificate validation
                RemoteCertificateValidationCallback = insecureSkipVerify
                    ? (sender, cert, chain, errors) => true
                    : null
            },

            // Use system proxy settings
            UseProxy = true,

            // Per Syncthing: "announcements are few and far between, so don't keep the connection open"
            // For announce client: set very short idle timeout to close connections quickly
            // For query client: keep connections alive briefly for potential reuse
            PooledConnectionIdleTimeout = disableKeepAlive
                ? TimeSpan.FromMilliseconds(1)  // Close almost immediately
                : TimeSpan.FromSeconds(1),

            // Maximum lifetime for connections
            PooledConnectionLifetime = disableKeepAlive
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromMinutes(1)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Per Syncthing: "DisableKeepAlives: true" - use Connection: close header for announce client
        if (disableKeepAlive)
        {
            client.DefaultRequestHeaders.ConnectionClose = true;
        }

        // Request HTTP/2 by default
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        return client;
    }

    /// <summary>
    /// Gets whether global discovery is running.
    /// </summary>
    public bool IsRunning => _cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested;

    /// <summary>
    /// Gets the configured discovery servers.
    /// </summary>
    public IReadOnlyList<string> DiscoveryServers => _discoveryServers;

    /// <summary>
    /// Starts global discovery announcements.
    /// </summary>
    public Task StartAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            _logger.LogWarning("Global discovery is already running");
            return Task.CompletedTask;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token);

        var addressList = addresses.ToList();
        _announceTask = AnnounceLoopAsync(addressList, linkedCts.Token);

        _logger.LogInformation("Global discovery started with {Count} servers", _discoveryServers.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops global discovery.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
        }

        if (_announceTask != null)
        {
            try { await _announceTask; } catch { }
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _logger.LogInformation("Global discovery stopped");
    }

    /// <summary>
    /// Announces our device to all discovery servers.
    /// Returns the minimum reannounce interval suggested by any successful server.
    /// </summary>
    public async Task<int?> AnnounceAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        var addressList = addresses.ToList();

        // Per Syncthing: don't announce if no addresses
        if (addressList.Count == 0)
        {
            SetError(null);
            _logger.LogDebug("No addresses to announce - skipping global discovery announcement");
            return null;
        }

        var announcement = new DiscoveryAnnouncement
        {
            Addresses = SanitizeRelayAddresses(addressList)
        };

        var tasks = _discoveryServers.Select(server =>
            AnnounceToServerAsync(server, announcement, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r.Success);

        _logger.LogDebug("Announced to {Success}/{Total} discovery servers",
            successCount, _discoveryServers.Count);

        // Return the minimum reannounce interval from successful servers
        // This allows the server to control the announcement rate
        var reannounceSeconds = results
            .Where(r => r.Success && r.ReannounceAfterSeconds.HasValue)
            .Select(r => r.ReannounceAfterSeconds!.Value)
            .DefaultIfEmpty(0)
            .Min();

        return reannounceSeconds > 0 ? reannounceSeconds : null;
    }

    /// <summary>
    /// Sanitizes relay addresses by keeping only the 'id' query parameter.
    /// Per Syncthing global.go sanitizeRelayAddresses().
    /// </summary>
    private static List<string> SanitizeRelayAddresses(List<string> addresses)
    {
        var result = new List<string>(addresses.Count);
        foreach (var addr in addresses)
        {
            if (addr.StartsWith("relay://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(addr);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var id = query["id"];

                    // Rebuild URL with only 'id' parameter
                    var builder = new UriBuilder(uri)
                    {
                        Query = string.IsNullOrEmpty(id) ? "" : $"id={Uri.EscapeDataString(id)}"
                    };
                    result.Add(builder.Uri.ToString());
                }
                catch
                {
                    // If parsing fails, include original
                    result.Add(addr);
                }
            }
            else
            {
                result.Add(addr);
            }
        }
        return result;
    }

    /// <summary>
    /// Looks up a device by ID on all discovery servers.
    /// </summary>
    /// <remarks>
    /// Protocol verification against Syncthing (lib/discover/global.go Lookup):
    /// - Returns lookupError with 1-hour cache if noLookup is set
    /// - Caches negative results (not found) using Retry-After header duration
    /// - Merges addresses from all servers
    /// - Returns null if device not found (but still caches negative result)
    /// </remarks>
    /// <exception cref="LookupError">Thrown when lookups are disabled (noLookup mode)</exception>
    public async Task<DiscoveredDevice?> LookupAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        // Per Syncthing global.go: if noLookup, return error with 1 hour cache
        if (_noLookup)
        {
            throw new LookupError("lookups not supported", TimeSpan.FromHours(1));
        }

        // Check cache first (includes both positive and negative cache entries)
        lock (_cacheLock)
        {
            if (_lookupCache.TryGetValue(deviceId, out var cached) &&
                cached.ExpiresAt > DateTime.UtcNow)
            {
                // If this is a negative cache entry, return null (device not found is cached)
                if (cached.IsNegative)
                {
                    _logger.LogDebug("Using cached negative lookup for device: {DeviceId} (error: {Error})",
                        deviceId, cached.ErrorMessage);
                    return null;
                }

                _logger.LogDebug("Using cached lookup for device: {DeviceId}", deviceId);
                return cached.Device;
            }
        }

        // Query all servers in parallel
        var tasks = _discoveryServers.Select(server =>
            LookupOnServerAsync(server, deviceId, cancellationToken));

        var results = await Task.WhenAll(tasks);

        // Collect successful results (with addresses)
        var successfulResults = results
            .Where(r => r is { Addresses.Count: > 0 })
            .ToList();

        // Collect error results with cache durations (for negative caching)
        var errorResults = results
            .Where(r => r is { Addresses.Count: 0, CacheForSeconds: not null })
            .ToList();

        if (successfulResults.Count == 0)
        {
            _logger.LogDebug("Device not found on any discovery server: {DeviceId}", deviceId);

            // Cache negative result using the longest Retry-After duration from servers
            // Per Syncthing: Retry-After header indicates how long to cache the error
            var maxCacheSeconds = errorResults
                .Where(r => r!.CacheForSeconds.HasValue)
                .Select(r => r!.CacheForSeconds!.Value)
                .DefaultIfEmpty(0)
                .Max();

            if (maxCacheSeconds > 0)
            {
                lock (_cacheLock)
                {
                    _lookupCache[deviceId] = new CachedLookup
                    {
                        Device = null,
                        ExpiresAt = DateTime.UtcNow.AddSeconds(maxCacheSeconds),
                        IsNegative = true,
                        ErrorMessage = "device not found"
                    };
                }
                _logger.LogDebug("Cached negative lookup for {DeviceId} for {Seconds}s",
                    deviceId, maxCacheSeconds);
            }

            return null;
        }

        // Merge addresses from all servers
        var allAddresses = successfulResults
            .SelectMany(r => r!.Addresses)
            .Distinct()
            .ToList();

        var discoveredDevice = new DiscoveredDevice
        {
            DeviceId = deviceId,
            Addresses = allAddresses,
            LastSeen = DateTime.UtcNow,
            Source = DiscoverySource.Global
        };

        // Cache the positive result
        lock (_cacheLock)
        {
            _lookupCache[deviceId] = new CachedLookup
            {
                Device = discoveredDevice,
                ExpiresAt = DateTime.UtcNow.AddSeconds(_lookupCacheSeconds),
                IsNegative = false,
                ErrorMessage = null
            };
        }

        _logger.LogDebug("Found device {DeviceId} with {Count} addresses via global discovery",
            deviceId, allAddresses.Count);

        if (DeviceDiscovered != null)
        {
            await DeviceDiscovered.Invoke(discoveredDevice);
        }

        return discoveredDevice;
    }

    /// <summary>
    /// Clears the lookup cache.
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _lookupCache.Clear();
        }
    }

    /// <summary>
    /// Removes a device from the cache.
    /// </summary>
    public void InvalidateCache(string deviceId)
    {
        lock (_cacheLock)
        {
            _lookupCache.Remove(deviceId);
        }
    }

    /// <summary>
    /// Announce loop that respects server-provided intervals.
    /// Per Syncthing global.go Serve():
    /// - Initial delay: 5 seconds
    /// - Reannounce-After header: server-recommended interval on success
    /// - Retry-After header: server-recommended interval on error
    /// - Default reannounce: 30 minutes
    /// - Error retry: 5 minutes
    /// </summary>
    private async Task AnnounceLoopAsync(List<string> addresses, CancellationToken cancellationToken)
    {
        // Per Syncthing: initial delay of 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            int nextIntervalSeconds;

            try
            {
                // Make announcement and get server-recommended interval
                var reannounceAfter = await AnnounceAsync(addresses, cancellationToken);

                // Use server-provided interval, or default
                nextIntervalSeconds = reannounceAfter ?? _announceIntervalSeconds;

                _logger.LogDebug("Next global discovery announcement in {Seconds}s", nextIntervalSeconds);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send global discovery announcement");

                // Use error retry interval
                nextIntervalSeconds = ErrorRetryIntervalSeconds;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(nextIntervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Announces to a single discovery server.
    /// Per Syncthing protocol (lib/discover/global.go):
    /// - POST to server URL directly (no device query parameter)
    /// - Device identity proven via TLS client certificate
    /// - JSON body: {"addresses": [...]}
    /// - Response 2xx = success
    /// - Reannounce-After header: server-recommended reannounce interval
    /// - Retry-After header: server-recommended retry interval on error
    /// </summary>
    private async Task<AnnounceResult> AnnounceToServerAsync(
        string server,
        DiscoveryAnnouncement announcement,
        CancellationToken cancellationToken)
    {
        try
        {
            // Per Syncthing global.go sendAnnouncement():
            // POST directly to the server URL without device query parameter
            // Device identity is established via TLS client certificate
            var url = server.TrimEnd('/');
            var json = JsonSerializer.Serialize(announcement);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _announceClient.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                SetError(null);
                _logger.LogDebug("Successfully announced to: {Server}", server);

                // Check for Reannounce-After header (server-recommended interval)
                // Per Syncthing: "The server has a recommendation on when we should reannounce"
                int? reannounceAfter = null;
                if (response.Headers.TryGetValues("Reannounce-After", out var reannounceValues))
                {
                    var headerValue = reannounceValues.FirstOrDefault();
                    if (int.TryParse(headerValue, out var secs) && secs > 0)
                    {
                        reannounceAfter = secs;
                        _logger.LogDebug("Server {Server} sets Reannounce-After: {Seconds}s",
                            server, secs);
                    }
                }

                return new AnnounceResult(true, reannounceAfter, null);
            }

            // Handle error response
            SetError(response.ReasonPhrase ?? "Unknown error");
            _logger.LogDebug("Failed to announce to {Server}: {StatusCode}",
                server, response.StatusCode);

            // Check for Retry-After header on error
            // Per Syncthing: "The server has a recommendation on when we should retry"
            int? retryAfter = null;
            if (response.Headers.TryGetValues("Retry-After", out var retryValues))
            {
                var headerValue = retryValues.FirstOrDefault();
                if (int.TryParse(headerValue, out var secs) && secs > 0)
                {
                    retryAfter = secs;
                    _logger.LogDebug("Server {Server} sets Retry-After: {Seconds}s",
                        server, secs);
                }
            }

            return new AnnounceResult(false, null, retryAfter);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            _logger.LogDebug(ex, "Error announcing to server: {Server}", server);
            return new AnnounceResult(false, null, null);
        }
    }

    /// <summary>
    /// Result of an announce attempt with server-provided timing hints.
    /// </summary>
    private record AnnounceResult(bool Success, int? ReannounceAfterSeconds, int? RetryAfterSeconds);

    /// <summary>
    /// Looks up a device on a single discovery server.
    /// Per Syncthing protocol (lib/discover/global.go Lookup()):
    /// - GET to server URL with ?device={deviceId} query parameter
    /// - No client certificate needed for lookups
    /// - Retry-After header indicates cache duration on error
    /// </summary>
    /// <remarks>
    /// Protocol verification against Syncthing (lib/discover/global.go):
    /// - On non-OK status: Check Retry-After header and return error with cache duration
    /// - Per Syncthing: "if secs, atoiErr := strconv.Atoi(resp.Header.Get("Retry-After")); atoiErr == nil && secs > 0"
    /// - Returns lookupError with msg=resp.Status and cacheFor=Retry-After seconds
    /// - On success: Parse JSON {"addresses": [...]} response
    /// </remarks>
    private async Task<LookupResult?> LookupOnServerAsync(
        string server,
        string deviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Per Syncthing global.go Lookup():
            // GET with device query parameter
            var url = $"{server.TrimEnd('/')}?device={Uri.EscapeDataString(deviceId)}";
            var response = await _queryClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Per Syncthing: Check Retry-After header and return error with cache duration
                // "if secs, atoiErr := strconv.Atoi(resp.Header.Get("Retry-After")); atoiErr == nil && secs > 0 {
                //     err = &lookupError{msg: resp.Status, cacheFor: time.Duration(secs) * time.Second}"
                int? cacheForSeconds = null;
                if (response.Headers.TryGetValues("Retry-After", out var retryValues))
                {
                    var headerValue = retryValues.FirstOrDefault();
                    if (int.TryParse(headerValue, out var secs) && secs > 0)
                    {
                        cacheForSeconds = secs;
                    }
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("Device not found on {Server}: {DeviceId} (cache for {CacheFor}s)",
                        server, deviceId, cacheForSeconds);
                }
                else
                {
                    _logger.LogDebug("Lookup failed on {Server}: {StatusCode} (cache for {CacheFor}s)",
                        server, response.StatusCode, cacheForSeconds);
                }

                // Return an empty result with cache duration for negative caching
                // Per Syncthing: error responses should be cached according to Retry-After
                return new LookupResult(new List<string>(), cacheForSeconds, response.ReasonPhrase);
            }

            var addresses = await response.Content.ReadFromJsonAsync<DiscoveryLookupResult>(cancellationToken: cancellationToken);
            return addresses != null ? new LookupResult(addresses.Addresses, null, null) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error looking up device on server: {Server}", server);
            return null;
        }
    }

    /// <summary>
    /// Result of a lookup attempt with optional cache duration.
    /// Per Syncthing global.go lookupError pattern - errors include cache duration.
    /// </summary>
    /// <param name="Addresses">List of addresses found (empty if not found/error)</param>
    /// <param name="CacheForSeconds">How long to cache this result (from Retry-After header)</param>
    /// <param name="ErrorMessage">Error message if this is an error result</param>
    private record LookupResult(List<string> Addresses, int? CacheForSeconds, string? ErrorMessage);

    /// <summary>
    /// Sets the error state.
    /// Per Syncthing global.go errorHolder pattern.
    /// </summary>
    private void SetError(string? error)
    {
        lock (_errorLock)
        {
            _lastError = error;
        }
    }

    /// <summary>
    /// Gets the current error, if any.
    /// </summary>
    public string? Error
    {
        get
        {
            lock (_errorLock)
            {
                return _lastError;
            }
        }
    }

    /// <summary>
    /// Gets whether the client has a TLS certificate configured for authentication.
    /// Without a certificate, announcements will fail.
    /// </summary>
    public bool HasClientCertificate => _clientCertificate != null;

    /// <summary>
    /// Gets whether lookups are disabled (announce-only mode).
    /// Per Syncthing global.go ?nolookup option.
    /// </summary>
    public bool NoLookup => _noLookup;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _announceClient.Dispose();
        _queryClient.Dispose();
    }

    private class DiscoveryAnnouncement
    {
        [JsonPropertyName("addresses")]
        public List<string> Addresses { get; set; } = new();
    }

    private class DiscoveryLookupResult
    {
        [JsonPropertyName("addresses")]
        public List<string> Addresses { get; set; } = new();

        [JsonPropertyName("seen")]
        public DateTime? Seen { get; set; }
    }

    private class CachedLookup
    {
        public DiscoveredDevice? Device { get; set; }
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// Whether this is a negative cache entry (device not found or error).
        /// </summary>
        public bool IsNegative { get; set; }

        /// <summary>
        /// Error message if this is a negative cache entry.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}

/// <summary>
/// A lookup error with cache validity time attached.
/// Per Syncthing global.go lookupError pattern.
/// </summary>
/// <remarks>
/// Protocol verification against Syncthing (lib/discover/global.go):
/// - lookupError struct has msg (string) and cacheFor (time.Duration)
/// - Error() returns the message
/// - CacheFor() returns how long the error should be cached
/// - Used to cache negative results (device not found, server errors)
/// </remarks>
public class LookupError : Exception
{
    /// <summary>
    /// Creates a new lookup error.
    /// </summary>
    /// <param name="message">Error message</param>
    /// <param name="cacheFor">How long to cache this error</param>
    public LookupError(string message, TimeSpan cacheFor) : base(message)
    {
        CacheFor = cacheFor;
    }

    /// <summary>
    /// How long this error should be cached.
    /// Per Syncthing: Retry-After header value on error responses.
    /// </summary>
    public TimeSpan CacheFor { get; }
}
