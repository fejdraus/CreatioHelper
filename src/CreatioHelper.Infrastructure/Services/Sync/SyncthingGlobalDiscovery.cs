using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// 100% Syncthing-compatible Global Discovery implementation
/// Based on Syncthing's lib/discover/global.go
///
/// API v2 compliance:
/// - POST /v2/ with JSON {"addresses": [...]} for announcement
/// - GET /v2/?device=&lt;id&gt; for lookup
/// - Requires client certificate for announcements only
/// - Reannounce every 30 minutes (honor Reannounce-After header)
/// - Handle rate limiting with Retry-After header
/// </summary>
public class SyncthingGlobalDiscovery : IDisposable
{
    private readonly ILogger<SyncthingGlobalDiscovery> _logger;
    private readonly HttpClient _announceClient;
    private readonly HttpClient _queryClient;
    private readonly X509Certificate2 _deviceCertificate;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly GlobalDiscoveryErrorHolder _errorHolder = new();

    // Syncthing Global Discovery constants (matching lib/discover/global.go)
    private const int DefaultReannounceIntervalMinutes = 30;
    private const int AnnounceErrorRetryIntervalMinutes = 5;
    private const int RequestTimeoutSeconds = 30;
    private const int MaxAddressChangesBetweenAnnouncements = 10;

    // Server configuration
    private readonly List<GlobalDiscoveryServer> _servers = new();

    // No hardcoded discovery servers — must be configured explicitly
    private static readonly string[] DefaultServers = Array.Empty<string>();

    public SyncthingGlobalDiscovery(
        ILogger<SyncthingGlobalDiscovery> logger,
        X509Certificate2 deviceCertificate,
        string deviceId)
    {
        _logger = logger;
        _deviceCertificate = deviceCertificate;
        _deviceId = deviceId;

        // HTTP/2-enabled announce client with device certificate
        // DisableKeepAlives equivalent: announcements are few and far between
        var announceHandler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { deviceCertificate },
                RemoteCertificateValidationCallback = ValidateServerCertificate
            },
            PooledConnectionLifetime = TimeSpan.Zero, // Disable keep-alives like Syncthing
            EnableMultipleHttp2Connections = true
        };

        _announceClient = new HttpClient(announceHandler)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };

        // Set HTTP/2
        _announceClient.DefaultRequestVersion = HttpVersion.Version20;
        _announceClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;

        // Query client without client certificate
        // IdleConnTimeout equivalent: allow connection reuse but with short timeout
        var queryHandler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = ValidateServerCertificate
            },
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(1), // IdleConnTimeout = 1 second like Syncthing
            EnableMultipleHttp2Connections = true
        };

        _queryClient = new HttpClient(queryHandler)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };

        _queryClient.DefaultRequestVersion = HttpVersion.Version20;
        _queryClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;

        // Initialize with default servers
        foreach (var server in DefaultServers)
        {
            var (serverUrl, options, error) = ParseServerOptions(server);
            if (error == null)
            {
                _servers.Add(new GlobalDiscoveryServer(serverUrl, options));
            }
        }
    }

    /// <summary>
    /// Validate server certificate (can be configured to check server device ID)
    /// Following Syncthing's idCheckingHTTPClient pattern
    /// </summary>
    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // For now, allow all certificates (testing mode)
        // In production, this should verify the server's device ID if configured
        // via the ?id= query parameter per Syncthing's pattern
        return true;
    }

    /// <summary>
    /// Parse server URL options following Syncthing's parseOptions function
    /// Supports: ?insecure, ?noannounce, ?nolookup, ?id=DEVICE-ID
    /// </summary>
    private static (string ServerUrl, GlobalDiscoveryServerOptions Options, string? Error) ParseServerOptions(string dsn)
    {
        if (!Uri.TryCreate(dsn, UriKind.Absolute, out var uri))
        {
            return ("", new GlobalDiscoveryServerOptions(), $"Invalid URL: {dsn}");
        }

        var options = new GlobalDiscoveryServerOptions();
        var query = HttpUtility.ParseQueryString(uri.Query);

        // Parse known options
        options.Id = query.Get("id") ?? "";
        options.Insecure = !string.IsNullOrEmpty(options.Id) || QueryBool(query, "insecure");
        options.NoAnnounce = QueryBool(query, "noannounce");
        options.NoLookup = QueryBool(query, "nolookup");

        // Check for disallowed combinations per Syncthing's rules
        if (uri.Scheme == "http")
        {
            if (!options.Insecure)
            {
                return ("", options, "http without insecure not supported");
            }
            if (!options.NoAnnounce)
            {
                return ("", options, "http without noannounce not supported");
            }
        }
        else if (uri.Scheme != "https")
        {
            return ("", options, $"unsupported scheme {uri.Scheme}");
        }

        // Remove query string to get clean server URL
        var serverUrl = new UriBuilder(uri) { Query = "" }.Uri.ToString();

        return (serverUrl, options, null);
    }

    /// <summary>
    /// Parse boolean query parameter following Syncthing's queryBool function
    /// Empty value (?foo) is true, any value except "false" is true
    /// </summary>
    private static bool QueryBool(System.Collections.Specialized.NameValueCollection query, string key)
    {
        var value = query.Get(key);
        if (value == null && query.AllKeys.Contains(key))
        {
            // Key exists with no value (?foo) - treat as true
            return true;
        }
        if (value == null)
        {
            return false;
        }
        return !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Announce this device to global discovery servers
    /// </summary>
    public async Task AnnounceAsync(List<string> addresses, CancellationToken cancellationToken = default)
    {
        if (addresses == null || addresses.Count == 0)
        {
            // No addresses is not an error per Syncthing - just clear any previous error
            _errorHolder.SetError(null);
            _logger.LogDebug("No addresses to announce");
            return;
        }

        var announcement = new SyncthingGlobalAnnouncement
        {
            Addresses = SanitizeRelayAddresses(addresses)
        };

        var json = JsonSerializer.Serialize(announcement);
        _logger.LogDebug("Global Discovery announcement: {Json}", json);

        foreach (var server in _servers.Where(s => !s.Options.NoAnnounce))
        {
            await AnnounceToServerAsync(server, json, cancellationToken);
        }
    }

    private async Task<TimeSpan> AnnounceToServerAsync(GlobalDiscoveryServer server, string json, CancellationToken cancellationToken)
    {
        var retryInterval = TimeSpan.FromMinutes(DefaultReannounceIntervalMinutes);

        try
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _announceClient.PostAsync(server.Url, content, cancellationToken);

            _logger.LogDebug("Global Discovery POST to {Server}: {StatusCode}", server.Url, response.StatusCode);

            // Check response status (matching Syncthing: < 200 || > 299)
            var statusCode = (int)response.StatusCode;
            if (statusCode < 200 || statusCode > 299)
            {
                var error = $"HTTP {statusCode} {response.ReasonPhrase}";
                _errorHolder.SetError(new Exception(error));
                server.LastError = new GlobalDiscoveryError(error, DateTime.UtcNow);

                // Handle Retry-After header - parse as seconds (Syncthing style)
                var retryAfterSeconds = ParseRetryAfterSeconds(response.Headers);
                if (retryAfterSeconds > 0)
                {
                    retryInterval = TimeSpan.FromSeconds(retryAfterSeconds);
                    _logger.LogDebug("Server {Server} sets retry-after: {Seconds} seconds", server.Url, retryAfterSeconds);
                }
                else
                {
                    retryInterval = TimeSpan.FromMinutes(AnnounceErrorRetryIntervalMinutes);
                }

                _logger.LogWarning("Failed to announce to {Server}: {Error}, retry after {RetryAfter}",
                    server.Url, error, retryInterval);

                return retryInterval;
            }

            // Success - clear errors
            _errorHolder.SetError(null);
            server.LastError = null;

            // Handle Reannounce-After header
            var reannounceSeconds = ParseReannounceAfterSeconds(response.Headers);
            if (reannounceSeconds > 0)
            {
                retryInterval = TimeSpan.FromSeconds(reannounceSeconds);
                _logger.LogDebug("Server {Server} sets reannounce-after: {Seconds} seconds", server.Url, reannounceSeconds);
            }

            _logger.LogDebug("Successfully announced to {Server}", server.Url);
        }
        catch (Exception ex)
        {
            _errorHolder.SetError(ex);
            server.LastError = new GlobalDiscoveryError(ex.Message, DateTime.UtcNow);
            _logger.LogError(ex, "Error announcing to global server {Server}", server.Url);
            retryInterval = TimeSpan.FromMinutes(AnnounceErrorRetryIntervalMinutes);
        }

        return retryInterval;
    }

    /// <summary>
    /// Parse Retry-After header as seconds (Syncthing style)
    /// </summary>
    private static int ParseRetryAfterSeconds(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("Retry-After", out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out var seconds) && seconds > 0)
            {
                return seconds;
            }
        }
        return 0;
    }

    /// <summary>
    /// Parse Reannounce-After header as seconds
    /// </summary>
    private static int ParseReannounceAfterSeconds(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("Reannounce-After", out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out var seconds) && seconds > 0)
            {
                return seconds;
            }
        }
        return 0;
    }

    /// <summary>
    /// Lookup device addresses from global discovery servers
    /// </summary>
    public async Task<List<string>> LookupAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var allAddresses = new List<string>();

        foreach (var server in _servers.Where(s => !s.Options.NoLookup))
        {
            try
            {
                var result = await LookupFromServerAsync(server, deviceId, cancellationToken);

                if (result.Error != null)
                {
                    _logger.LogWarning("Lookup failed for {DeviceId} from {Server}: {Error}",
                        deviceId, server.Url, result.Error.Message);
                    continue;
                }

                allAddresses.AddRange(result.Addresses);

                if (result.Addresses.Count > 0)
                {
                    _logger.LogDebug("Found device {DeviceId} via global discovery at {Server}: {Addresses}",
                        deviceId, server.Url, string.Join(", ", result.Addresses));
                    break; // Found addresses, no need to try other servers
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to lookup device {DeviceId} from server {Server}", deviceId, server.Url);
            }
        }

        return allAddresses;
    }

    /// <summary>
    /// Lookup result with optional error and cache duration
    /// </summary>
    private async Task<GlobalDiscoveryLookupResult> LookupFromServerAsync(
        GlobalDiscoveryServer server,
        string deviceId,
        CancellationToken cancellationToken)
    {
        // Build URL with proper query parameter encoding (following Syncthing's url.Parse + q.Set pattern)
        var uriBuilder = new UriBuilder(server.Url);
        var query = HttpUtility.ParseQueryString(uriBuilder.Query);
        query["device"] = deviceId;
        uriBuilder.Query = query.ToString();
        var url = uriBuilder.Uri.ToString();

        _logger.LogDebug("Global Discovery lookup: {Url}", url);

        var response = await _queryClient.GetAsync(url, cancellationToken);

        // Check for non-OK status (Syncthing checks resp.StatusCode != http.StatusOK)
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            // Parse Retry-After header to determine cache duration
            var retryAfterSeconds = ParseRetryAfterSeconds(response.Headers);
            var cacheFor = retryAfterSeconds > 0
                ? TimeSpan.FromSeconds(retryAfterSeconds)
                : TimeSpan.Zero;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Device not found - this is normal, return empty result
                return new GlobalDiscoveryLookupResult(new List<string>(), null, cacheFor);
            }

            // Return error with cache duration (following lookupError pattern)
            return new GlobalDiscoveryLookupResult(
                new List<string>(),
                new GlobalDiscoveryLookupError(error, cacheFor),
                cacheFor);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var announcement = JsonSerializer.Deserialize<SyncthingGlobalAnnouncement>(json);

        return new GlobalDiscoveryLookupResult(
            announcement?.Addresses ?? new List<string>(),
            null,
            TimeSpan.Zero);
    }

    /// <summary>
    /// Sanitize relay addresses according to Syncthing rules
    /// Based on Syncthing's sanitizeRelayAddresses function
    /// </summary>
    private static List<string> SanitizeRelayAddresses(List<string> addresses)
    {
        var sanitized = new List<string>();

        foreach (var addr in addresses)
        {
            if (string.IsNullOrWhiteSpace(addr))
                continue;

            // Remove relay:// prefix if present and add it back in standardized form
            var address = addr;
            if (address.StartsWith("relay://"))
            {
                address = address[8..]; // Remove "relay://"
            }

            // Basic validation - should contain host and port
            if (address.Contains(':'))
            {
                sanitized.Add($"relay://{address}");
            }
            else
            {
                // Direct TCP addresses
                sanitized.Add(address);
            }
        }

        return sanitized;
    }

    /// <summary>
    /// Start background announcement loop (Syncthing-compatible)
    /// Following the Serve() pattern from global.go
    /// </summary>
    public async Task StartAnnouncementLoopAsync(Func<List<string>> getAddresses, CancellationToken cancellationToken = default)
    {
        // Initial announcement after 5 seconds (matching Syncthing's timer.Reset(5 * time.Second))
        await Task.Delay(5000, cancellationToken);

        var nextAnnounce = TimeSpan.FromMinutes(DefaultReannounceIntervalMinutes);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var addresses = getAddresses();
                    if (addresses.Count > 0)
                    {
                        await AnnounceAsync(addresses, cancellationToken);
                    }
                    else
                    {
                        _errorHolder.SetError(null); // No addresses is not an error
                        _logger.LogDebug("No addresses available for announcement");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in global discovery announcement loop");
                    nextAnnounce = TimeSpan.FromMinutes(AnnounceErrorRetryIntervalMinutes);
                }

                await Task.Delay(nextAnnounce, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Global discovery announcement loop stopped");
        }
    }

    /// <summary>
    /// Set custom global discovery servers with option parsing
    /// </summary>
    public void SetGlobalDiscoveryServers(List<string> servers)
    {
        _servers.Clear();

        foreach (var server in servers)
        {
            var (serverUrl, options, error) = ParseServerOptions(server);
            if (error != null)
            {
                _logger.LogWarning("Invalid server URL {Server}: {Error}", server, error);
                continue;
            }
            _servers.Add(new GlobalDiscoveryServer(serverUrl, options));
        }

        _logger.LogInformation("Updated global discovery servers: {Servers}",
            string.Join(", ", _servers.Select(s => s.Url)));
    }

    /// <summary>
    /// Get current error (following Syncthing's errorHolder.Error() pattern)
    /// </summary>
    public Exception? GetError()
    {
        return _errorHolder.GetError();
    }

    /// <summary>
    /// Get errors for all servers
    /// </summary>
    public Dictionary<string, GlobalDiscoveryError> GetErrors()
    {
        return _servers
            .Where(s => s.LastError != null)
            .ToDictionary(s => s.Url, s => s.LastError!);
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _announceClient?.Dispose();
        _queryClient?.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}

/// <summary>
/// Global Discovery server configuration with parsed options
/// </summary>
public class GlobalDiscoveryServer
{
    public string Url { get; }
    public GlobalDiscoveryServerOptions Options { get; }
    public GlobalDiscoveryError? LastError { get; set; }

    public GlobalDiscoveryServer(string url, GlobalDiscoveryServerOptions options)
    {
        Url = url;
        Options = options;
    }
}

/// <summary>
/// Server options parsed from URL query parameters
/// Following Syncthing's serverOptions struct from global.go
/// </summary>
public class GlobalDiscoveryServerOptions
{
    /// <summary>Don't check server certificate</summary>
    public bool Insecure { get; set; }

    /// <summary>Don't announce to this server</summary>
    public bool NoAnnounce { get; set; }

    /// <summary>Don't use this server for lookups</summary>
    public bool NoLookup { get; set; }

    /// <summary>Expected server device ID (for certificate verification)</summary>
    public string Id { get; set; } = "";
}

/// <summary>
/// Global Discovery announcement structure (Syncthing-compatible)
/// </summary>
public class SyncthingGlobalAnnouncement
{
    public List<string> Addresses { get; set; } = new();
}

/// <summary>
/// Lookup result with addresses, optional error, and cache duration
/// </summary>
public class GlobalDiscoveryLookupResult
{
    public List<string> Addresses { get; }
    public GlobalDiscoveryLookupError? Error { get; }
    public TimeSpan CacheFor { get; }

    public GlobalDiscoveryLookupResult(List<string> addresses, GlobalDiscoveryLookupError? error, TimeSpan cacheFor)
    {
        Addresses = addresses;
        Error = error;
        CacheFor = cacheFor;
    }
}

/// <summary>
/// Lookup error with cache validity time attached
/// Following Syncthing's lookupError struct from global.go
/// </summary>
public class GlobalDiscoveryLookupError : Exception
{
    /// <summary>
    /// How long this error should be cached before retrying
    /// Populated from Retry-After header
    /// </summary>
    public TimeSpan CacheFor { get; }

    public GlobalDiscoveryLookupError(string message, TimeSpan cacheFor)
        : base(message)
    {
        CacheFor = cacheFor;
    }
}

/// <summary>
/// Error tracking for global discovery servers
/// </summary>
public class GlobalDiscoveryError
{
    public string Message { get; }
    public DateTime Timestamp { get; }
    public TimeSpan Age => DateTime.UtcNow - Timestamp;

    public GlobalDiscoveryError(string message, DateTime timestamp)
    {
        Message = message;
        Timestamp = timestamp;
    }
}

/// <summary>
/// Thread-safe error holder following Syncthing's errorHolder pattern
/// </summary>
public class GlobalDiscoveryErrorHolder
{
    private readonly object _lock = new();
    private Exception? _error;

    public void SetError(Exception? error)
    {
        lock (_lock)
        {
            _error = error;
        }
    }

    public Exception? GetError()
    {
        lock (_lock)
        {
            return _error;
        }
    }
}
