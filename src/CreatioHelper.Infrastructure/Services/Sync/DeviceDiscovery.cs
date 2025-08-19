using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Device discovery implementation based on Syncthing discovery mechanism
/// Inspired by Syncthing's lib/discover package (local.go and global.go)
/// </summary>
public class DeviceDiscovery : IDeviceDiscovery, IDisposable
{
    private readonly ILogger<DeviceDiscovery> _logger;
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _discoveredDevices = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private UdpClient? _localDiscoveryClient;
    private readonly HttpClient _httpClient = new();
    private readonly int _localDiscoveryPort = 21027; // Syncthing's default
    private List<string> _globalDiscoveryServers = new()
    {
        "https://discovery.syncthing.net/v2/",
        "https://discovery-v4.syncthing.net/v2/",
        "https://discovery-v6.syncthing.net/v2/"
    };

    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    public DeviceDiscovery(ILogger<DeviceDiscovery> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await StartLocalDiscoveryAsync();
        StartGlobalDiscoveryLoop();
        
        _logger.LogInformation("Device discovery started");
    }

    public Task StopAsync()
    {
        _cancellationTokenSource.Cancel();
        _localDiscoveryClient?.Dispose();
        _httpClient.Dispose();
        
        _logger.LogInformation("Device discovery stopped");
        return Task.CompletedTask;
    }

    public async Task AnnounceAsync(SyncDevice localDevice, List<string> addresses)
    {
        await AnnounceLocalAsync(localDevice, addresses);
        await AnnounceGlobalAsync(localDevice, addresses);
    }

    public async Task<List<DiscoveredDevice>> DiscoverAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var devices = new List<DiscoveredDevice>();
        
        // Check local cache first
        if (_discoveredDevices.TryGetValue(deviceId, out var cachedDevice))
        {
            devices.Add(cachedDevice);
        }
        
        // Query global discovery servers
        var globalDevices = await QueryGlobalDiscoveryAsync(deviceId, cancellationToken);
        devices.AddRange(globalDevices);
        
        return devices;
    }

    public Task SetGlobalDiscoveryServersAsync(List<string> servers)
    {
        _globalDiscoveryServers = servers;
        return Task.CompletedTask;
    }

    public Task SetLocalDiscoveryPortAsync(int port)
    {
        // Would need to restart local discovery with new port
        _logger.LogInformation("Local discovery port set to {Port}", port);
        return Task.CompletedTask;
    }

    private Task StartLocalDiscoveryAsync()
    {
        try
        {
            _localDiscoveryClient = new UdpClient(_localDiscoveryPort);
            _localDiscoveryClient.EnableBroadcast = true;
            
            _ = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _localDiscoveryClient.ReceiveAsync();
                        await ProcessLocalDiscoveryMessage(result.Buffer, result.RemoteEndPoint);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in local discovery receive loop");
                    }
                }
            });
            
            _logger.LogInformation("Local discovery listening on port {Port}", _localDiscoveryPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start local discovery");
        }
        
        return Task.CompletedTask;
    }

    private Task ProcessLocalDiscoveryMessage(byte[] data, IPEndPoint remoteEndPoint)
    {
        try
        {
            var message = Encoding.UTF8.GetString(data);
            var announcement = JsonSerializer.Deserialize<LocalDiscoveryAnnouncement>(message);
            
            if (announcement?.DeviceId == null) return Task.CompletedTask;
            
            var device = new DiscoveredDevice
            {
                DeviceId = announcement.DeviceId,
                Addresses = announcement.Addresses,
                LastSeen = DateTime.UtcNow,
                Source = DiscoverySource.Local
            };
            
            _discoveredDevices.AddOrUpdate(announcement.DeviceId, device, (key, existing) =>
            {
                existing.Addresses = announcement.Addresses;
                existing.LastSeen = DateTime.UtcNow;
                return existing;
            });
            
            DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(device));
            
            _logger.LogDebug("Discovered device {DeviceId} locally at {Addresses}", 
                announcement.DeviceId, string.Join(", ", announcement.Addresses));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing local discovery message from {RemoteEndPoint}", remoteEndPoint);
        }
        
        return Task.CompletedTask;
    }

    private async Task AnnounceLocalAsync(SyncDevice localDevice, List<string> addresses)
    {
        if (_localDiscoveryClient == null) return;
        
        try
        {
            var announcement = new LocalDiscoveryAnnouncement
            {
                DeviceId = localDevice.DeviceId,
                Addresses = addresses
            };
            
            var message = JsonSerializer.Serialize(announcement);
            var data = Encoding.UTF8.GetBytes(message);
            
            // Broadcast to local network
            var broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, _localDiscoveryPort);
            await _localDiscoveryClient.SendAsync(data, broadcastEndPoint);
            
            // Also send to IPv6 multicast
            var ipv6MulticastAddress = IPAddress.Parse("ff12::8384");
            var ipv6EndPoint = new IPEndPoint(ipv6MulticastAddress, _localDiscoveryPort);
            await _localDiscoveryClient.SendAsync(data, ipv6EndPoint);
            
            _logger.LogDebug("Announced device {DeviceId} locally", localDevice.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error announcing device locally");
        }
    }

    private async Task AnnounceGlobalAsync(SyncDevice localDevice, List<string> addresses)
    {
        foreach (var server in _globalDiscoveryServers)
        {
            try
            {
                var announcement = new GlobalDiscoveryAnnouncement
                {
                    Device = localDevice.DeviceId,
                    Addresses = addresses
                };
                
                var json = JsonSerializer.Serialize(announcement);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync($"{server}announce", content);
                response.EnsureSuccessStatusCode();
                
                _logger.LogDebug("Announced device {DeviceId} to global server {Server}", localDevice.DeviceId, server);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to announce to global server {Server}", server);
            }
        }
    }

    private void StartGlobalDiscoveryLoop()
    {
        _ = Task.Run(async () =>
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Clean up old discoveries
                    var cutoff = DateTime.UtcNow.AddMinutes(-5);
                    var expiredDevices = _discoveredDevices
                        .Where(kvp => kvp.Value.LastSeen < cutoff)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    
                    foreach (var deviceId in expiredDevices)
                    {
                        _discoveredDevices.TryRemove(deviceId, out _);
                    }
                    
                    await Task.Delay(TimeSpan.FromMinutes(1), _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in global discovery cleanup loop");
                }
            }
        });
    }

    private async Task<List<DiscoveredDevice>> QueryGlobalDiscoveryAsync(string deviceId, CancellationToken cancellationToken)
    {
        var devices = new List<DiscoveredDevice>();
        
        foreach (var server in _globalDiscoveryServers)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{server}lookup?device={deviceId}", cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<GlobalDiscoveryResponse>(json);
                
                if (result?.Addresses != null && result.Addresses.Any())
                {
                    var device = new DiscoveredDevice
                    {
                        DeviceId = deviceId,
                        Addresses = result.Addresses,
                        LastSeen = DateTime.UtcNow,
                        Source = DiscoverySource.Global
                    };
                    
                    devices.Add(device);
                    
                    _logger.LogDebug("Found device {DeviceId} via global discovery at {Addresses}", 
                        deviceId, string.Join(", ", result.Addresses));
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query global discovery server {Server}", server);
            }
        }
        
        return devices;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _localDiscoveryClient?.Dispose();
        _httpClient.Dispose();
        _cancellationTokenSource.Dispose();
    }
}

// Message structures for discovery protocol
public class LocalDiscoveryAnnouncement
{
    public string DeviceId { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
}

public class GlobalDiscoveryAnnouncement
{
    public string Device { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
}

public class GlobalDiscoveryResponse
{
    public List<string> Addresses { get; set; } = new();
}