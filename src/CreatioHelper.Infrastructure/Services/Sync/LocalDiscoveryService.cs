using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Local discovery service for finding Syncthing-like devices on the local network
/// Uses UDP broadcast/multicast for device announcement and discovery
/// Compatible with Syncthing's local discovery protocol
/// </summary>
public class LocalDiscoveryService : IDeviceDiscovery
{
    private readonly ILogger<LocalDiscoveryService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _currentDeviceId;
    private readonly int _currentPort;
    private readonly int _discoveryPort;
    private Timer? _announceTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Dictionary<string, DiscoveredDevice> _discoveredDevices = new();
    
    private UdpClient? _udpClient;
    private UdpClient? _broadcastClient;
    private CancellationTokenSource _cancellationTokenSource = new();
    private bool _isRunning;

    // Syncthing local discovery magic bytes
    private static readonly byte[] MagicBytes = { 0x2E, 0xA7, 0xD9, 0x0B, 0x54, 0x25, 0x2A, 0x4F };
    private const int DefaultDiscoveryPort = 21027;
    
    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    public LocalDiscoveryService(
        ILogger<LocalDiscoveryService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        
        // Try environment variable first, then configuration, then throw
        _currentDeviceId = Environment.GetEnvironmentVariable("Sync__DeviceId") 
            ?? _configuration["Sync:DeviceId"]
            ?? throw new InvalidOperationException("Sync__DeviceId environment variable or Sync:DeviceId configuration is required");
        
        // Try environment variable first, then configuration, then default
        var portString = Environment.GetEnvironmentVariable("Sync__Port") 
            ?? _configuration["Sync:Port"] 
            ?? "22000";
        if (!int.TryParse(portString, out _currentPort))
            _currentPort = 22000;
        
        // Discovery port
        var discoveryPortString = Environment.GetEnvironmentVariable("Sync__DiscoveryPort") 
            ?? _configuration["Sync:DiscoveryPort"] 
            ?? DefaultDiscoveryPort.ToString();
        if (!int.TryParse(discoveryPortString, out _discoveryPort))
            _discoveryPort = DefaultDiscoveryPort;
        
        _logger.LogInformation("Local discovery service initialized for device {DeviceId} on port {Port}, discovery port {DiscoveryPort}", 
            _currentDeviceId, _currentPort, _discoveryPort);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return Task.CompletedTask;
        
        _logger.LogInformation("Starting local discovery service on port {DiscoveryPort}", _discoveryPort);
        
        try
        {
            // Setup UDP listener for incoming announcements
            _udpClient = new UdpClient(_discoveryPort);
            _udpClient.EnableBroadcast = true;
            
            // Setup UDP client for broadcasts
            _broadcastClient = new UdpClient();
            _broadcastClient.EnableBroadcast = true;
            
            _isRunning = true;
            
            // Start listening for announcements
            _ = Task.Run(async () => await ListenForAnnouncementsAsync(_cancellationTokenSource.Token));
            
            // Start periodic announcements every 30 seconds
            _announceTimer?.Dispose();
            _announceTimer = new Timer(AnnounceDevice, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
            
            _logger.LogInformation("Local discovery service started successfully");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start local discovery service");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        
        _logger.LogInformation("Stopping local discovery service");
        
        _isRunning = false;
        _cancellationTokenSource.Cancel();
        
        _announceTimer?.Dispose();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _broadcastClient?.Close();
        _broadcastClient?.Dispose();
        
        await Task.Delay(100); // Give time for cleanup
        
        _logger.LogInformation("Local discovery service stopped");
    }

    public async Task AnnounceAsync(SyncDevice localDevice, List<string> addresses)
    {
        if (!_isRunning) return;
        
        await _semaphore.WaitAsync();
        try
        {
            await BroadcastAnnouncementAsync(addresses);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<List<DiscoveredDevice>> DiscoverAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var devices = new List<DiscoveredDevice>();
            
            if (_discoveredDevices.TryGetValue(deviceId, out var device))
            {
                // Check if device is still fresh (last seen within 2 minutes)
                if (DateTime.UtcNow - device.LastSeen < TimeSpan.FromMinutes(2))
                {
                    devices.Add(device);
                }
                else
                {
                    _discoveredDevices.Remove(deviceId);
                }
            }
            
            return devices;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SetGlobalDiscoveryServersAsync(List<string> servers)
    {
        // Local discovery doesn't use global servers, but we implement for interface compliance
        _logger.LogDebug("Global discovery servers set (not used by local discovery): {Servers}", string.Join(", ", servers));
        await Task.CompletedTask;
    }

    public async Task SetLocalDiscoveryPortAsync(int port)
    {
        if (port != _discoveryPort)
        {
            _logger.LogWarning("Cannot change discovery port while service is running. Current: {CurrentPort}, Requested: {NewPort}", 
                _discoveryPort, port);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Listen for incoming UDP announcements from other devices
    /// </summary>
    private async Task ListenForAnnouncementsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting to listen for local discovery announcements");
        
        try
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var result = await _udpClient!.ReceiveAsync();
                    await ProcessAnnouncementAsync(result);
                }
                catch (ObjectDisposedException)
                {
                    // UDP client disposed, normal shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error receiving UDP announcement");
                    await Task.Delay(1000, cancellationToken); // Brief delay before retrying
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        
        _logger.LogDebug("Stopped listening for local discovery announcements");
    }

    /// <summary>
    /// Process received announcement
    /// </summary>
    private async Task ProcessAnnouncementAsync(UdpReceiveResult result)
    {
        try
        {
            var data = result.Buffer;
            
            // Check magic bytes
            if (data.Length < MagicBytes.Length || !data.Take(MagicBytes.Length).SequenceEqual(MagicBytes))
            {
                return; // Not a Syncthing announcement
            }
            
            // Extract JSON payload
            var jsonData = data.Skip(MagicBytes.Length).ToArray();
            var json = Encoding.UTF8.GetString(jsonData);
            
            var announcement = JsonSerializer.Deserialize<LocalDeviceAnnouncement>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (announcement == null || string.IsNullOrEmpty(announcement.DeviceId))
            {
                return;
            }
            
            // Don't process our own announcements
            if (announcement.DeviceId == _currentDeviceId)
            {
                return;
            }
            
            _logger.LogDebug("Received local discovery announcement from device {DeviceId} at {Address}", 
                announcement.DeviceId, result.RemoteEndPoint);
            
            // Update discovered devices
            await _semaphore.WaitAsync();
            try
            {
                var device = new DiscoveredDevice
                {
                    DeviceId = announcement.DeviceId,
                    Addresses = announcement.Addresses ?? new List<string>(),
                    LastSeen = DateTime.UtcNow,
                    Source = DiscoverySource.Local
                };
                
                // Add sender's address if not in announcement
                var senderAddress = $"tcp://{result.RemoteEndPoint.Address}:{announcement.Port}";
                if (!device.Addresses.Contains(senderAddress))
                {
                    device.Addresses.Add(senderAddress);
                }
                
                _discoveredDevices[announcement.DeviceId] = device;
                
                // Fire discovery event
                DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(device));
                
                _logger.LogInformation("Discovered device {DeviceId} locally with {Count} addresses", 
                    announcement.DeviceId, device.Addresses.Count);
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing local discovery announcement from {RemoteEndPoint}", result.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Broadcast announcement to local network
    /// </summary>
    private async Task BroadcastAnnouncementAsync(List<string> addresses)
    {
        try
        {
            var announcement = new LocalDeviceAnnouncement
            {
                DeviceId = _currentDeviceId,
                Port = _currentPort,
                Addresses = addresses,
                Timestamp = DateTimeOffset.UtcNow
            };
            
            var json = JsonSerializer.Serialize(announcement);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            
            // Combine magic bytes with JSON
            var packet = new byte[MagicBytes.Length + jsonBytes.Length];
            Array.Copy(MagicBytes, 0, packet, 0, MagicBytes.Length);
            Array.Copy(jsonBytes, 0, packet, MagicBytes.Length, jsonBytes.Length);
            
            // Broadcast to subnet
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);
            await _broadcastClient!.SendAsync(packet, broadcastEndpoint);
            
            // Also send to multicast (Syncthing compatibility)
            var multicastEndpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), _discoveryPort);
            try
            {
                await _broadcastClient.SendAsync(packet, multicastEndpoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send multicast announcement (this is normal if multicast is disabled)");
            }
            
            _logger.LogDebug("Broadcasted local discovery announcement for device {DeviceId}", _currentDeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast local discovery announcement");
        }
    }

    /// <summary>
    /// Periodic device announcement
    /// </summary>
    private async void AnnounceDevice(object? state)
    {
        if (!_isRunning) return;
        
        try
        {
            var addresses = GetLocalAddresses();
            await BroadcastAnnouncementAsync(addresses);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during local device announcement");
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting local addresses");
        }
        
        return addresses;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cancellationTokenSource.Dispose();
        _semaphore.Dispose();
        _announceTimer?.Dispose();
    }
}

/// <summary>
/// Local device announcement structure
/// </summary>
public class LocalDeviceAnnouncement
{
    public string DeviceId { get; set; } = string.Empty;
    public int Port { get; set; }
    public List<string> Addresses { get; set; } = new();
    public DateTimeOffset Timestamp { get; set; }
}