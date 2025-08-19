using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Global discovery service for finding Syncthing-like devices over the internet
/// Implements device announcement and discovery without requiring direct P2P connections
/// Similar to Syncthing's global discovery mechanism
/// </summary>
public class GlobalDiscoveryService : IDeviceDiscovery
{
    private readonly ILogger<GlobalDiscoveryService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly Timer? _announceTimer;
    private readonly string _currentDeviceId;
    private readonly int _currentPort;
    private readonly List<string> _discoveryServers;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    
    // Default Syncthing-like discovery servers (можно настроить через конфигурацию)
    private static readonly List<string> DefaultDiscoveryServers = new()
    {
        "https://discovery-v4.syncthing.net",
        "https://discovery-v6.syncthing.net"
    };

    public GlobalDiscoveryService(
        ILogger<GlobalDiscoveryService> logger,
        IConfiguration configuration,
        HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
        
        _currentDeviceId = Environment.GetEnvironmentVariable("Sync__DeviceId") 
            ?? throw new InvalidOperationException("Sync__DeviceId environment variable is required");
        
        var portString = Environment.GetEnvironmentVariable("Sync__Port") ?? "22000";
        if (!int.TryParse(portString, out _currentPort))
            _currentPort = 22000;
            
        _discoveryServers = _configuration.GetSection("Sync:DiscoveryServers")
            .Get<List<string>>() ?? DefaultDiscoveryServers;
            
        // Announce ourselves every 30 minutes (like Syncthing)
        _announceTimer = new Timer(AnnounceDevice, null, TimeSpan.Zero, TimeSpan.FromMinutes(30));
        
        _logger.LogInformation("Global discovery service initialized for device {DeviceId} on port {Port}", 
            _currentDeviceId, _currentPort);
    }

    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting global discovery service");
        // Service starts automatically in constructor with timer
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping global discovery service");
        _announceTimer?.Dispose();
        await Task.CompletedTask;
    }

    public async Task AnnounceAsync(SyncDevice localDevice, List<string> addresses)
    {
        await _semaphore.WaitAsync();
        try
        {
            foreach (var server in _discoveryServers)
            {
                try
                {
                    await AnnounceToServerAsync(server, addresses);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to announce to discovery server {Server}", server);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<List<DiscoveredDevice>> DiscoverAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var addresses = await DiscoverDeviceAsync(deviceId);
        var devices = new List<DiscoveredDevice>();
        
        if (addresses.Any())
        {
            devices.Add(new DiscoveredDevice
            {
                DeviceId = deviceId,
                Addresses = addresses,
                LastSeen = DateTime.UtcNow,
                Source = DiscoverySource.Global
            });
        }
        
        return devices;
    }

    public async Task SetGlobalDiscoveryServersAsync(List<string> servers)
    {
        _discoveryServers.Clear();
        _discoveryServers.AddRange(servers);
        _logger.LogInformation("Updated global discovery servers: {Servers}", string.Join(", ", servers));
        await Task.CompletedTask;
    }

    public async Task SetLocalDiscoveryPortAsync(int port)
    {
        // Global discovery doesn't use local discovery port, but we implement for interface compliance
        _logger.LogDebug("Local discovery port set to {Port} (not used by global discovery)", port);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Discover devices by device ID through global discovery servers
    /// Returns list of addresses where the device can be reached
    /// </summary>
    public async Task<List<string>> DiscoverDeviceAsync(string deviceId)
    {
        await _semaphore.WaitAsync();
        try
        {
            var addresses = new List<string>();
            
            foreach (var server in _discoveryServers)
            {
                try
                {
                    var discoveredAddresses = await QueryDiscoveryServerAsync(server, deviceId);
                    addresses.AddRange(discoveredAddresses);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query discovery server {Server} for device {DeviceId}", 
                        server, deviceId);
                }
            }

            // Remove duplicates and prioritize direct addresses
            var uniqueAddresses = addresses.Distinct().ToList();
            
            _logger.LogInformation("Discovered {Count} addresses for device {DeviceId}: {Addresses}", 
                uniqueAddresses.Count, deviceId, string.Join(", ", uniqueAddresses));
                
            return uniqueAddresses;
        }
        finally
        {
            _semaphore.Release();
        }
    }


    /// <summary>
    /// Announce current device to global discovery servers
    /// </summary>
    private async void AnnounceDevice(object? state)
    {
        try
        {
            await _semaphore.WaitAsync();
            
            var localAddresses = GetLocalAddresses();
            
            foreach (var server in _discoveryServers)
            {
                try
                {
                    await AnnounceToServerAsync(server, localAddresses);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to announce to discovery server {Server}", server);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during device announcement");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Query specific discovery server for device addresses
    /// </summary>
    private async Task<List<string>> QueryDiscoveryServerAsync(string serverUrl, string deviceId)
    {
        var requestUrl = $"{serverUrl}/v2/?device={deviceId}";
        
        _logger.LogDebug("Querying discovery server {Server} for device {DeviceId}", serverUrl, deviceId);
        
        var response = await _httpClient.GetAsync(requestUrl);
        response.EnsureSuccessStatusCode();
        
        var content = await response.Content.ReadAsStringAsync();
        var discoveryResponse = JsonSerializer.Deserialize<DiscoveryResponse>(content);
        
        return discoveryResponse?.Addresses ?? new List<string>();
    }

    /// <summary>
    /// Announce current device to specific discovery server
    /// </summary>
    private async Task AnnounceToServerAsync(string serverUrl, List<string> addresses)
    {
        var announcement = new DeviceAnnouncement
        {
            DeviceId = _currentDeviceId,
            Addresses = addresses,
            Timestamp = DateTimeOffset.UtcNow
        };
        
        var requestUrl = $"{serverUrl}/v2/";
        
        _logger.LogDebug("Announcing device {DeviceId} to server {Server} with {Count} addresses", 
            _currentDeviceId, serverUrl, addresses.Count);
        
        var response = await _httpClient.PostAsJsonAsync(requestUrl, announcement);
        
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Successfully announced device to {Server}", serverUrl);
        }
        else
        {
            _logger.LogWarning("Failed to announce device to {Server}: {StatusCode}", 
                serverUrl, response.StatusCode);
        }
    }

    /// <summary>
    /// Get current device's local addresses
    /// </summary>
    private List<string> GetLocalAddresses()
    {
        var addresses = new List<string>();
        
        try
        {
            // Add localhost address
            addresses.Add($"tcp://127.0.0.1:{_currentPort}");
            
            // Add local network addresses
            var hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var address in hostEntry.AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    addresses.Add($"tcp://{address}:{_currentPort}");
                }
            }
            
            // Add external IP if configured
            var externalIp = _configuration["Sync:ExternalIP"];
            if (!string.IsNullOrEmpty(externalIp))
            {
                addresses.Add($"tcp://{externalIp}:{_currentPort}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting local addresses");
        }
        
        return addresses;
    }

    public void Dispose()
    {
        _announceTimer?.Dispose();
        _semaphore.Dispose();
    }
}

/// <summary>
/// Response from discovery server
/// </summary>
public class DiscoveryResponse
{
    public List<string> Addresses { get; set; } = new();
    public DateTimeOffset? LastSeen { get; set; }
}

/// <summary>
/// Device announcement to discovery server
/// </summary>
public class DeviceAnnouncement
{
    public string DeviceId { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public DateTimeOffset Timestamp { get; set; }
}