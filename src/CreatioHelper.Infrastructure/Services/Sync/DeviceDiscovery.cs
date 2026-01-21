using System.Buffers.Binary;
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
    private readonly int _localDiscoveryPort;
    private List<string> _globalDiscoveryServers = new()
    {
        "https://discovery.syncthing.net/v2/",
        "https://discovery-v4.syncthing.net/v2/",
        "https://discovery-v6.syncthing.net/v2/"
    };

    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    public DeviceDiscovery(ILogger<DeviceDiscovery> logger, int discoveryPort = 21027)
    {
        _logger = logger;
        _localDiscoveryPort = discoveryPort;
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

    // Syncthing local discovery protocol magic number (0x2EA7D90B)
    private const uint LocalDiscoveryMagic = 0x2EA7D90B;

    private Task ProcessLocalDiscoveryMessage(byte[] data, IPEndPoint remoteEndPoint)
    {
        try
        {
            if (data.Length < 4)
            {
                _logger.LogDebug("Local discovery message too short ({Length} bytes) from {RemoteEndPoint}",
                    data.Length, remoteEndPoint);
                return Task.CompletedTask;
            }

            // Check for Syncthing binary protocol (magic header)
            var magic = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
            if (magic == LocalDiscoveryMagic)
            {
                return ProcessSyncthingLocalDiscovery(data, remoteEndPoint);
            }

            // Check for JSON format (our own format or fallback)
            if (data[0] == (byte)'{')
            {
                return ProcessJsonLocalDiscovery(data, remoteEndPoint);
            }

            _logger.LogDebug("Unknown local discovery format from {RemoteEndPoint}, magic: 0x{Magic:X8}",
                remoteEndPoint, magic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing local discovery message from {RemoteEndPoint}", remoteEndPoint);
        }

        return Task.CompletedTask;
    }

    private Task ProcessSyncthingLocalDiscovery(byte[] data, IPEndPoint remoteEndPoint)
    {
        // Syncthing local discovery v2 format:
        // - Magic (4 bytes): 0x2EA7D90B
        // - Announce message (protobuf encoded)
        //
        // Announce message structure:
        // - DeviceID (32 bytes raw, displayed as base32)
        // - Addresses (list of strings)
        // - InstanceID (8 bytes)

        if (data.Length < 44) // 4 magic + 32 deviceId + 8 instanceId minimum
        {
            _logger.LogDebug("Syncthing discovery message too short ({Length} bytes)", data.Length);
            return Task.CompletedTask;
        }

        try
        {
            // Skip magic (4 bytes)
            var offset = 4;

            // Read device ID (32 bytes) - convert to Syncthing format (base32 with dashes)
            var deviceIdBytes = data.AsSpan(offset, 32);
            var deviceId = FormatDeviceId(deviceIdBytes.ToArray());
            offset += 32;

            // Read instance ID (8 bytes) - just skip it for now
            offset += 8;

            // Rest of the message contains addresses in a simple length-prefixed format
            var addresses = new List<string>();

            // Read number of addresses (4 bytes, big-endian)
            if (offset + 4 <= data.Length)
            {
                var addressCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                offset += 4;

                for (int i = 0; i < addressCount && offset < data.Length; i++)
                {
                    // Read address length (4 bytes)
                    if (offset + 4 > data.Length) break;
                    var addrLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                    offset += 4;

                    // Read address string
                    if (offset + addrLen > data.Length || addrLen <= 0 || addrLen > 256) break;
                    var addr = Encoding.UTF8.GetString(data, offset, addrLen);
                    addresses.Add(addr);
                    offset += addrLen;
                }
            }

            // If no addresses parsed, use the remote endpoint
            if (addresses.Count == 0)
            {
                addresses.Add($"tcp://{remoteEndPoint.Address}:22000");
            }

            var device = new DiscoveredDevice
            {
                DeviceId = deviceId,
                Addresses = addresses,
                LastSeen = DateTime.UtcNow,
                Source = DiscoverySource.Local
            };

            _discoveredDevices.AddOrUpdate(deviceId, device, (key, existing) =>
            {
                existing.Addresses = addresses;
                existing.LastSeen = DateTime.UtcNow;
                return existing;
            });

            DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(device));

            _logger.LogDebug("Discovered Syncthing device {DeviceId} locally at {Addresses}",
                deviceId, string.Join(", ", addresses));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Syncthing local discovery from {RemoteEndPoint}", remoteEndPoint);
        }

        return Task.CompletedTask;
    }

    private Task ProcessJsonLocalDiscovery(byte[] data, IPEndPoint remoteEndPoint)
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

        _logger.LogDebug("Discovered device {DeviceId} locally (JSON) at {Addresses}",
            announcement.DeviceId, string.Join(", ", announcement.Addresses));

        return Task.CompletedTask;
    }

    private static string FormatDeviceId(byte[] deviceIdBytes)
    {
        // Convert 32 bytes to Syncthing device ID format (base32 with dashes)
        // Syncthing uses Luhn mod N check characters, but for display we can use simple base32
        var base32 = ToBase32(deviceIdBytes);

        // Insert dashes every 7 characters (Syncthing format: XXXXXXX-XXXXXXX-...)
        var parts = new List<string>();
        for (int i = 0; i < base32.Length; i += 7)
        {
            var len = Math.Min(7, base32.Length - i);
            parts.Add(base32.Substring(i, len));
        }

        return string.Join("-", parts);
    }

    private static string ToBase32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder((data.Length * 8 + 4) / 5);

        int buffer = 0;
        int bitsInBuffer = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                result.Append(alphabet[(buffer >> bitsInBuffer) & 0x1F]);
            }
        }

        if (bitsInBuffer > 0)
        {
            result.Append(alphabet[(buffer << (5 - bitsInBuffer)) & 0x1F]);
        }

        return result.ToString();
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