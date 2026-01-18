using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// Global device discovery using Syncthing discovery servers.
/// Implements the Syncthing global discovery protocol (v3).
/// </summary>
public class GlobalDiscovery : IAsyncDisposable
{
    private readonly ILogger<GlobalDiscovery> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _deviceId;
    private readonly List<string> _discoveryServers;
    private readonly int _announceIntervalSeconds;
    private readonly int _lookupCacheSeconds;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _announceTask;
    private readonly Dictionary<string, CachedLookup> _lookupCache = new();
    private readonly object _cacheLock = new();

    // Default Syncthing discovery servers
    private static readonly string[] DefaultDiscoveryServers =
    {
        "https://discovery.syncthing.net/v2",
        "https://discovery-v4.syncthing.net/v2",
        "https://discovery-v6.syncthing.net/v2"
    };

    /// <summary>
    /// Event fired when a device is discovered via global discovery.
    /// </summary>
    public event Func<DiscoveredDevice, Task>? DeviceDiscovered;

    public GlobalDiscovery(
        ILogger<GlobalDiscovery> logger,
        string deviceId,
        IEnumerable<string>? discoveryServers = null,
        int announceIntervalSeconds = 1800,
        int lookupCacheSeconds = 300)
    {
        _logger = logger;
        _deviceId = deviceId;
        _discoveryServers = discoveryServers?.ToList() ?? DefaultDiscoveryServers.ToList();
        _announceIntervalSeconds = announceIntervalSeconds;
        _lookupCacheSeconds = lookupCacheSeconds;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
    /// </summary>
    public async Task AnnounceAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        var announcement = new DiscoveryAnnouncement
        {
            Addresses = addresses.ToList()
        };

        var tasks = _discoveryServers.Select(server =>
            AnnounceToServerAsync(server, announcement, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);

        _logger.LogDebug("Announced to {Success}/{Total} discovery servers",
            successCount, _discoveryServers.Count);
    }

    /// <summary>
    /// Looks up a device by ID on all discovery servers.
    /// </summary>
    public async Task<DiscoveredDevice?> LookupAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_lookupCache.TryGetValue(deviceId, out var cached) &&
                cached.ExpiresAt > DateTime.UtcNow)
            {
                _logger.LogDebug("Using cached lookup for device: {DeviceId}", deviceId);
                return cached.Device;
            }
        }

        // Query all servers in parallel
        var tasks = _discoveryServers.Select(server =>
            LookupOnServerAsync(server, deviceId, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var successfulResults = results
            .Where(r => r != null && r.Addresses.Count > 0)
            .ToList();

        if (successfulResults.Count == 0)
        {
            _logger.LogDebug("Device not found on any discovery server: {DeviceId}", deviceId);
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
            DiscoveredAt = DateTime.UtcNow,
            DiscoveryMethod = DiscoveryMethod.Global
        };

        // Cache the result
        lock (_cacheLock)
        {
            _lookupCache[deviceId] = new CachedLookup
            {
                Device = discoveredDevice,
                ExpiresAt = DateTime.UtcNow.AddSeconds(_lookupCacheSeconds)
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

    private async Task AnnounceLoopAsync(List<string> addresses, CancellationToken cancellationToken)
    {
        // Initial announcement
        try
        {
            await AnnounceAsync(addresses, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send initial global discovery announcement");
        }

        // Periodic announcements
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_announceIntervalSeconds), cancellationToken);
                await AnnounceAsync(addresses, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send global discovery announcement");
            }
        }
    }

    private async Task<bool> AnnounceToServerAsync(
        string server,
        DiscoveryAnnouncement announcement,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{server.TrimEnd('/')}/?device={Uri.EscapeDataString(_deviceId)}";
            var json = JsonSerializer.Serialize(announcement);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Successfully announced to: {Server}", server);
                return true;
            }

            _logger.LogDebug("Failed to announce to {Server}: {StatusCode}",
                server, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error announcing to server: {Server}", server);
            return false;
        }
    }

    private async Task<DiscoveryLookupResult?> LookupOnServerAsync(
        string server,
        string deviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{server.TrimEnd('/')}/?device={Uri.EscapeDataString(deviceId)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("Device not found on {Server}: {DeviceId}", server, deviceId);
                }
                else
                {
                    _logger.LogDebug("Lookup failed on {Server}: {StatusCode}",
                        server, response.StatusCode);
                }
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<DiscoveryLookupResult>(cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error looking up device on server: {Server}", server);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient.Dispose();
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
    }
}
