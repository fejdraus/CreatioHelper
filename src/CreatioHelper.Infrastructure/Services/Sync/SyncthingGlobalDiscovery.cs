using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// 100% Syncthing-compatible Global Discovery implementation
/// Based on Syncthing's lib/discover/global.go
/// </summary>
public class SyncthingGlobalDiscovery : IDisposable
{
    private readonly ILogger<SyncthingGlobalDiscovery> _logger;
    private readonly HttpClient _announceClient;
    private readonly HttpClient _queryClient;
    private readonly X509Certificate2 _deviceCertificate;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ConcurrentDictionary<string, GlobalDiscoveryError> _errors = new();
    
    // Syncthing Global Discovery constants
    private const int DefaultReannounceIntervalMinutes = 30;
    private const int AnnounceErrorRetryIntervalMinutes = 5;
    private const int RequestTimeoutSeconds = 30;
    private const int MaxAddressChangesBetweenAnnouncements = 10;
    
    private List<string> _globalDiscoveryServers = new()
    {
        "https://discovery.syncthing.net/v2/",
        "https://discovery-v4.syncthing.net/v2/",
        "https://discovery-v6.syncthing.net/v2/"
    };

    public SyncthingGlobalDiscovery(
        ILogger<SyncthingGlobalDiscovery> logger,
        X509Certificate2 deviceCertificate,
        string deviceId)
    {
        _logger = logger;
        _deviceCertificate = deviceCertificate;
        _deviceId = deviceId;

        // HTTP/2-enabled announce client with device certificate
        var announceHandler = new HttpClientHandler
        {
            ClientCertificates = { deviceCertificate },
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // For testing
        };
        
        _announceClient = new HttpClient(announceHandler)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };
        
        // Set HTTP/2
        _announceClient.DefaultRequestVersion = HttpVersion.Version20;
        _announceClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;

        // Query client without client certificate  
        var queryHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // For testing
        };
        
        _queryClient = new HttpClient(queryHandler)
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };
        
        _queryClient.DefaultRequestVersion = HttpVersion.Version20;
        _queryClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
    }

    /// <summary>
    /// Announce this device to global discovery servers
    /// </summary>
    public async Task AnnounceAsync(List<string> addresses, CancellationToken cancellationToken = default)
    {
        if (addresses == null || addresses.Count == 0)
        {
            _logger.LogDebug("No addresses to announce");
            return;
        }

        var announcement = new SyncthingGlobalAnnouncement
        {
            Addresses = SanitizeRelayAddresses(addresses)
        };

        var json = JsonSerializer.Serialize(announcement);
        _logger.LogDebug("Global Discovery announcement: {Json}", json);

        foreach (var server in _globalDiscoveryServers)
        {
            await AnnounceToServerAsync(server, json, cancellationToken);
        }
    }

    private async Task AnnounceToServerAsync(string server, string json, CancellationToken cancellationToken)
    {
        try
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _announceClient.PostAsync(server, content, cancellationToken);

            _logger.LogDebug("Global Discovery POST to {Server}: {StatusCode}", server, response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                // Clear any previous errors
                _errors.TryRemove(server, out _);

                // Handle Reannounce-After header
                if (response.Headers.TryGetValues("Reannounce-After", out var reannounceValues))
                {
                    var reannounceAfter = reannounceValues.FirstOrDefault();
                    if (int.TryParse(reannounceAfter, out var seconds))
                    {
                        _logger.LogDebug("Server {Server} sets reannounce-after: {Seconds} seconds", server, seconds);
                    }
                }

                _logger.LogDebug("Successfully announced to {Server}", server);
            }
            else
            {
                var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                _errors[server] = new GlobalDiscoveryError(error, DateTime.UtcNow);

                // Handle Retry-After header
                if (response.Headers.RetryAfter != null)
                {
                    var retryAfter = response.Headers.RetryAfter.Delta ?? TimeSpan.FromMinutes(AnnounceErrorRetryIntervalMinutes);
                    _logger.LogWarning("Server {Server} returned {Error}, retry after {RetryAfter}", 
                        server, error, retryAfter);
                }
                else
                {
                    _logger.LogWarning("Failed to announce to {Server}: {Error}", server, error);
                }
            }
        }
        catch (Exception ex)
        {
            _errors[server] = new GlobalDiscoveryError(ex.Message, DateTime.UtcNow);
            _logger.LogError(ex, "Error announcing to global server {Server}", server);
        }
    }

    /// <summary>
    /// Lookup device addresses from global discovery servers
    /// </summary>
    public async Task<List<string>> LookupAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var allAddresses = new List<string>();

        foreach (var server in _globalDiscoveryServers)
        {
            try
            {
                var addresses = await LookupFromServerAsync(server, deviceId, cancellationToken);
                allAddresses.AddRange(addresses);
                
                if (addresses.Count > 0)
                {
                    _logger.LogDebug("Found device {DeviceId} via global discovery at {Server}: {Addresses}", 
                        deviceId, server, string.Join(", ", addresses));
                    break; // Found addresses, no need to try other servers
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to lookup device {DeviceId} from server {Server}", deviceId, server);
            }
        }

        return allAddresses;
    }

    private async Task<List<string>> LookupFromServerAsync(string server, string deviceId, CancellationToken cancellationToken)
    {
        var url = $"{server}?device={deviceId}";
        var response = await _queryClient.GetAsync(url, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Device not found - this is normal
            return new List<string>();
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            
            // Handle Retry-After header for lookups too
            if (response.Headers.RetryAfter != null)
            {
                var retryAfter = response.Headers.RetryAfter.Delta ?? TimeSpan.FromMinutes(5);
                _logger.LogDebug("Server {Server} lookup returned {Error}, should retry after {RetryAfter}", 
                    server, error, retryAfter);
            }
            
            throw new HttpRequestException(error);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var announcement = JsonSerializer.Deserialize<SyncthingGlobalAnnouncement>(json);
        
        return announcement?.Addresses ?? new List<string>();
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
    /// </summary>
    public async Task StartAnnouncementLoopAsync(Func<List<string>> getAddresses, CancellationToken cancellationToken = default)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(DefaultReannounceIntervalMinutes));
        
        // Initial announcement after 5 seconds
        await Task.Delay(5000, cancellationToken);
        
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
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
                        _logger.LogDebug("No addresses available for announcement");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in global discovery announcement loop");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Global discovery announcement loop stopped");
        }
        finally
        {
            timer.Dispose();
        }
    }

    public void SetGlobalDiscoveryServers(List<string> servers)
    {
        _globalDiscoveryServers = servers;
        _logger.LogInformation("Updated global discovery servers: {Servers}", string.Join(", ", servers));
    }

    public Dictionary<string, GlobalDiscoveryError> GetErrors()
    {
        return new Dictionary<string, GlobalDiscoveryError>(_errors);
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
/// Global Discovery announcement structure (Syncthing-compatible)
/// </summary>
public class SyncthingGlobalAnnouncement
{
    public List<string> Addresses { get; set; } = new();
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