using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Sync;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// Unified device discovery manager that coordinates local and global discovery.
/// Based on Syncthing's discovery coordination from lib/discover/manager.go
///
/// Supports multiple global discovery backends:
/// - GlobalDiscovery: Newer implementation with TLS client certificate support
/// - SyncthingGlobalDiscovery: Legacy implementation for backwards compatibility
/// </summary>
public class DiscoveryManager : IDiscoveryManager
{
    private readonly ILogger<DiscoveryManager> _logger;
    private readonly DiscoveryCache _cache;
    private readonly ILocalDiscovery? _localDiscovery;
    private readonly SyncthingGlobalDiscovery? _syncthingGlobalDiscovery;
    private readonly GlobalDiscovery? _globalDiscovery;
    private readonly ConcurrentDictionary<string, List<string>> _staticAddresses = new();
    private readonly SemaphoreSlim _lookupSemaphore = new(1, 1);

    private bool _isRunning;
    private bool _disposed;

    private DateTime _lastLocalAnnouncement = DateTime.MinValue;
    private DateTime _lastGlobalAnnouncement = DateTime.MinValue;
    private DateTime _lastReceived = DateTime.MinValue;

    public bool LocalDiscoveryEnabled => _localDiscovery != null;
    public bool GlobalDiscoveryEnabled => _globalDiscovery != null || _syncthingGlobalDiscovery != null;

    /// <summary>
    /// Returns true if using the newer GlobalDiscovery implementation with TLS support.
    /// </summary>
    public bool UsingTlsGlobalDiscovery => _globalDiscovery != null;

    public event EventHandler<DeviceDiscoveredArgs>? DeviceDiscovered;

    /// <summary>
    /// Creates a new DiscoveryManager with the newer GlobalDiscovery implementation.
    /// Preferred constructor for new code.
    /// </summary>
    public DiscoveryManager(
        ILogger<DiscoveryManager> logger,
        DiscoveryCache cache,
        ILocalDiscovery? localDiscovery = null,
        GlobalDiscovery? globalDiscovery = null)
    {
        _logger = logger;
        _cache = cache;
        _localDiscovery = localDiscovery;
        _globalDiscovery = globalDiscovery;
        _syncthingGlobalDiscovery = null;

        SubscribeToDiscoveryEvents();
    }

    /// <summary>
    /// Creates a new DiscoveryManager with the legacy SyncthingGlobalDiscovery.
    /// Provided for backwards compatibility.
    /// </summary>
    public DiscoveryManager(
        ILogger<DiscoveryManager> logger,
        DiscoveryCache cache,
        ILocalDiscovery? localDiscovery,
        SyncthingGlobalDiscovery? syncthingGlobalDiscovery)
    {
        _logger = logger;
        _cache = cache;
        _localDiscovery = localDiscovery;
        _syncthingGlobalDiscovery = syncthingGlobalDiscovery;
        _globalDiscovery = null;

        SubscribeToDiscoveryEvents();
    }

    /// <summary>
    /// Creates a new DiscoveryManager with both global discovery implementations.
    /// The newer GlobalDiscovery is preferred when available.
    /// </summary>
    public DiscoveryManager(
        ILogger<DiscoveryManager> logger,
        DiscoveryCache cache,
        ILocalDiscovery? localDiscovery,
        GlobalDiscovery? globalDiscovery,
        SyncthingGlobalDiscovery? syncthingGlobalDiscovery)
    {
        _logger = logger;
        _cache = cache;
        _localDiscovery = localDiscovery;
        _globalDiscovery = globalDiscovery;
        _syncthingGlobalDiscovery = syncthingGlobalDiscovery;

        SubscribeToDiscoveryEvents();
    }

    private void SubscribeToDiscoveryEvents()
    {
        // Subscribe to local discovery events
        if (_localDiscovery != null)
        {
            _localDiscovery.DeviceDiscovered += OnLocalDeviceDiscovered;
        }

        // Subscribe to global discovery events (newer implementation)
        if (_globalDiscovery != null)
        {
            _globalDiscovery.DeviceDiscovered += OnGlobalDeviceDiscovered;
        }
    }

    /// <summary>
    /// Handle device discovered via GlobalDiscovery (async event).
    /// </summary>
    private async Task OnGlobalDeviceDiscovered(DiscoveredDevice device)
    {
        _lastReceived = DateTime.UtcNow;

        // Add to cache
        _cache.AddPositive(device.DeviceId, device.Addresses, DiscoveryCacheSource.Global);

        // Raise event
        var addresses = device.Addresses
            .Select(a => new DiscoveredAddress
            {
                Address = a,
                Source = DiscoveryCacheSource.Global,
                Priority = CalculatePriority(a, DiscoveryCacheSource.Global),
                IsLan = IsLanAddress(a),
                DiscoveredAt = device.LastSeen
            })
            .ToList();

        DeviceDiscovered?.Invoke(this, new DeviceDiscoveredArgs(device.DeviceId, addresses, DiscoveryCacheSource.Global));

        await Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            return;

        _logger.LogInformation("Starting discovery manager");

        try
        {
            // Start local discovery
            if (_localDiscovery != null)
            {
                await _localDiscovery.StartAsync(cancellationToken);
                _logger.LogInformation("Local discovery started");
            }

            // Log global discovery availability
            // Note: GlobalDiscovery and SyncthingGlobalDiscovery are used on-demand for lookups
            // GlobalDiscovery can be started separately for announcements if needed
            if (_globalDiscovery != null)
            {
                _logger.LogInformation("Global discovery available (TLS-enabled, {ServerCount} servers)",
                    _globalDiscovery.DiscoveryServers.Count);
            }
            else if (_syncthingGlobalDiscovery != null)
            {
                _logger.LogInformation("Global discovery available (legacy mode)");
            }

            _isRunning = true;
            _logger.LogInformation("Discovery manager started (Local: {Local}, Global: {Global}, TLS: {TLS})",
                LocalDiscoveryEnabled, GlobalDiscoveryEnabled, UsingTlsGlobalDiscovery);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start discovery manager");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _logger.LogInformation("Stopping discovery manager");

        if (_localDiscovery != null)
        {
            await _localDiscovery.StopAsync();
        }

        // Stop GlobalDiscovery if running
        if (_globalDiscovery != null && _globalDiscovery.IsRunning)
        {
            await _globalDiscovery.StopAsync();
        }

        _isRunning = false;
        _logger.LogInformation("Discovery manager stopped");
    }

    public async Task<DiscoveryResult> LookupAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DiscoveryResult { DeviceId = deviceId };

        await _lookupSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Check cache first
            if (_cache.TryGet(deviceId, out var cacheEntry) && cacheEntry != null && !cacheEntry.IsNegative)
            {
                result.FromCache = true;
                result.Sources.Add(cacheEntry.Source);
                result.Addresses = cacheEntry.Addresses
                    .Select(a => new DiscoveredAddress
                    {
                        Address = a,
                        Source = cacheEntry.Source,
                        Priority = CalculatePriority(a, cacheEntry.Source),
                        IsLan = IsLanAddress(a),
                        DiscoveredAt = DateTime.UtcNow,
                        ExpiresAt = cacheEntry.ExpiresAt
                    })
                    .OrderBy(a => a.Priority)
                    .ToList();

                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;

                _logger.LogDebug("Cache hit for device {DeviceId}: {Count} addresses", deviceId, result.Addresses.Count);
                return result;
            }

            // 1. Check static addresses
            var staticAddresses = GetStaticAddresses(deviceId);
            if (staticAddresses.Any())
            {
                result.Sources.Add(DiscoveryCacheSource.Static);
                foreach (var addr in staticAddresses)
                {
                    result.Addresses.Add(new DiscoveredAddress
                    {
                        Address = addr,
                        Source = DiscoveryCacheSource.Static,
                        Priority = CalculatePriority(addr, DiscoveryCacheSource.Static),
                        IsLan = IsLanAddress(addr),
                        DiscoveredAt = DateTime.UtcNow
                    });
                }
            }

            // 2. Check local discovery cache
            if (_localDiscovery != null)
            {
                var localDevices = await _localDiscovery.DiscoverAsync(deviceId, cancellationToken);
                if (localDevices.Any())
                {
                    result.Sources.Add(DiscoveryCacheSource.Local);
                    foreach (var device in localDevices)
                    {
                        foreach (var addr in device.Addresses)
                        {
                            if (!result.Addresses.Any(a => a.Address == addr))
                            {
                                result.Addresses.Add(new DiscoveredAddress
                                {
                                    Address = addr,
                                    Source = DiscoveryCacheSource.Local,
                                    Priority = CalculatePriority(addr, DiscoveryCacheSource.Local),
                                    IsLan = IsLanAddress(addr),
                                    DiscoveredAt = device.LastSeen
                                });
                            }
                        }
                    }
                }
            }

            // 3. Try global discovery if we don't have enough addresses or need verification
            // Prefer the newer GlobalDiscovery (TLS-enabled) if available, fallback to legacy
            if ((result.Addresses.Count == 0 || !result.Sources.Contains(DiscoveryCacheSource.Local)) &&
                (_globalDiscovery != null || _syncthingGlobalDiscovery != null))
            {
                try
                {
                    List<string>? globalAddresses = null;
                    DateTime lastSeen = DateTime.UtcNow;

                    // Try the newer TLS-enabled GlobalDiscovery first
                    if (_globalDiscovery != null)
                    {
                        try
                        {
                            var discoveredDevice = await _globalDiscovery.LookupAsync(deviceId, cancellationToken);
                            if (discoveredDevice != null && discoveredDevice.Addresses.Any())
                            {
                                globalAddresses = discoveredDevice.Addresses;
                                lastSeen = discoveredDevice.LastSeen;
                                _logger.LogDebug("Global discovery (TLS) found device {DeviceId} with {Count} addresses",
                                    deviceId, globalAddresses.Count);
                            }
                        }
                        catch (LookupError lookupError)
                        {
                            // LookupError indicates lookups are disabled (noLookup mode)
                            // Cache the error duration and try fallback
                            _logger.LogDebug("Global discovery (TLS) lookup disabled: {Error}, cache for {CacheFor}",
                                lookupError.Message, lookupError.CacheFor);
                        }
                    }

                    // Fallback to legacy SyncthingGlobalDiscovery if TLS lookup failed
                    if (globalAddresses == null && _syncthingGlobalDiscovery != null)
                    {
                        globalAddresses = await _syncthingGlobalDiscovery.LookupAsync(deviceId, cancellationToken);
                        if (globalAddresses != null && globalAddresses.Any())
                        {
                            _logger.LogDebug("Global discovery (legacy) found device {DeviceId} with {Count} addresses",
                                deviceId, globalAddresses.Count);
                        }
                    }

                    if (globalAddresses != null && globalAddresses.Any())
                    {
                        result.Sources.Add(DiscoveryCacheSource.Global);
                        foreach (var addr in globalAddresses)
                        {
                            if (!result.Addresses.Any(a => a.Address == addr))
                            {
                                result.Addresses.Add(new DiscoveredAddress
                                {
                                    Address = addr,
                                    Source = DiscoveryCacheSource.Global,
                                    Priority = CalculatePriority(addr, DiscoveryCacheSource.Global),
                                    IsLan = IsLanAddress(addr),
                                    DiscoveredAt = lastSeen
                                });
                            }
                        }

                        // Cache the global result
                        _cache.AddPositive(deviceId, globalAddresses, DiscoveryCacheSource.Global);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Global discovery lookup failed for device {DeviceId}", deviceId);
                }
            }

            // Sort addresses by priority
            result.Addresses = result.Addresses.OrderBy(a => a.Priority).ToList();

            // Cache negative result if nothing found
            if (!result.Found)
            {
                _cache.AddNegative(deviceId, DiscoveryCacheSource.Global);
            }

            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;

            _logger.LogDebug("Discovery lookup for {DeviceId}: {Count} addresses from {Sources} in {Duration}ms",
                deviceId, result.Addresses.Count, string.Join(", ", result.Sources), result.Duration.TotalMilliseconds);

            return result;
        }
        finally
        {
            _lookupSemaphore.Release();
        }
    }

    public void AddStaticAddresses(string deviceId, IEnumerable<string> addresses)
    {
        var addressList = addresses.ToList();
        _staticAddresses.AddOrUpdate(deviceId, addressList, (_, existing) =>
        {
            var merged = new HashSet<string>(existing);
            merged.UnionWith(addressList);
            return merged.ToList();
        });

        // Add to cache
        _cache.AddPositive(deviceId, addressList, DiscoveryCacheSource.Static);

        _logger.LogInformation("Added {Count} static addresses for device {DeviceId}", addressList.Count, deviceId);
    }

    public void RemoveStaticAddresses(string deviceId)
    {
        _staticAddresses.TryRemove(deviceId, out _);
        _logger.LogInformation("Removed static addresses for device {DeviceId}", deviceId);
    }

    public DiscoveryStatus GetStatus()
    {
        var status = new DiscoveryStatus
        {
            IsRunning = _isRunning
        };

        // Local discovery status
        if (_localDiscovery != null)
        {
            status.LocalDiscovery = new LocalDiscoveryStatus
            {
                Enabled = true,
                Running = _isRunning,
                Port = 21027, // Default port
                DiscoveredDeviceCount = _cache.GetStatistics().LocalEntryCount,
                LastAnnouncement = _lastLocalAnnouncement,
                LastReceived = _lastReceived
            };
        }

        // Global discovery status - prefer TLS implementation if available
        if (_globalDiscovery != null)
        {
            // Use the newer TLS-enabled GlobalDiscovery
            var servers = _globalDiscovery.DiscoveryServers.ToList();
            var serverStatuses = new Dictionary<string, GlobalDiscoveryServerStatus>();

            // Check for errors on each server
            var currentError = _globalDiscovery.Error;
            if (currentError != null)
            {
                // If there's an error, mark all servers as having issues
                foreach (var server in servers)
                {
                    serverStatuses[server] = new GlobalDiscoveryServerStatus
                    {
                        Server = server,
                        Available = false,
                        Error = currentError,
                        FailureCount = 1
                    };
                }
            }

            status.GlobalDiscovery = new GlobalDiscoveryStatus
            {
                Enabled = true,
                Running = _globalDiscovery.IsRunning,
                Servers = servers,
                LastAnnouncement = _lastGlobalAnnouncement,
                ServerStatuses = serverStatuses
            };
        }
        else if (_syncthingGlobalDiscovery != null)
        {
            // Fallback to legacy SyncthingGlobalDiscovery
            var errors = _syncthingGlobalDiscovery.GetErrors();
            status.GlobalDiscovery = new GlobalDiscoveryStatus
            {
                Enabled = true,
                Running = _isRunning,
                Servers = new List<string>
                {
                    "https://discovery.syncthing.net/v2/",
                    "https://discovery-v4.syncthing.net/v2/",
                    "https://discovery-v6.syncthing.net/v2/"
                },
                LastAnnouncement = _lastGlobalAnnouncement,
                ServerStatuses = errors.ToDictionary(
                    e => e.Key,
                    e => new GlobalDiscoveryServerStatus
                    {
                        Server = e.Key,
                        Available = false,
                        Error = e.Value.Message,
                        FailureCount = 1
                    })
            };
        }

        // Static addresses status
        status.StaticAddresses = new StaticAddressStatus
        {
            DeviceCount = _staticAddresses.Count,
            TotalAddressCount = _staticAddresses.Values.Sum(v => v.Count)
        };

        return status;
    }

    public CacheStatistics GetCacheStatistics()
    {
        return _cache.GetStatistics();
    }

    private List<string> GetStaticAddresses(string deviceId)
    {
        return _staticAddresses.TryGetValue(deviceId, out var addresses) ? addresses : new List<string>();
    }

    private void OnLocalDeviceDiscovered(object? sender, DeviceDiscoveredEventArgs e)
    {
        _lastReceived = DateTime.UtcNow;

        // Add to cache
        _cache.AddPositive(e.Device.DeviceId, e.Device.Addresses, DiscoveryCacheSource.Local);

        // Raise event
        var addresses = e.Device.Addresses
            .Select(a => new DiscoveredAddress
            {
                Address = a,
                Source = DiscoveryCacheSource.Local,
                Priority = CalculatePriority(a, DiscoveryCacheSource.Local),
                IsLan = true,
                DiscoveredAt = e.Device.LastSeen
            })
            .ToList();

        DeviceDiscovered?.Invoke(this, new DeviceDiscoveredArgs(e.Device.DeviceId, addresses, DiscoveryCacheSource.Local));
    }

    /// <summary>
    /// Calculate connection priority (lower is better)
    /// Based on Syncthing's connection priority system
    /// </summary>
    private int CalculatePriority(string address, DiscoveryCacheSource source)
    {
        // Base priority by source
        var basePriority = source switch
        {
            DiscoveryCacheSource.Static => 0,
            DiscoveryCacheSource.Local => 10,
            DiscoveryCacheSource.Global => 20,
            _ => 50
        };

        // Protocol priority
        if (address.StartsWith("quic://"))
            basePriority -= 5;
        else if (address.StartsWith("tcp://"))
            basePriority += 0;
        else if (address.StartsWith("relay://"))
            basePriority += 80;

        // LAN vs WAN
        if (IsLanAddress(address))
            basePriority -= 10;

        return basePriority;
    }

    /// <summary>
    /// Check if an address is on a LAN (private network)
    /// Based on Syncthing's IsLAN function
    /// </summary>
    private static bool IsLanAddress(string address)
    {
        try
        {
            var uri = new Uri(address);
            var host = uri.Host.Trim('[', ']');

            if (!IPAddress.TryParse(host, out var ipAddress))
                return false;

            return IsLanIpAddress(ipAddress);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if IP address is on a LAN
    /// </summary>
    public static bool IsLanIpAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 127.0.0.0/8 (loopback)
            if (bytes[0] == 127) return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 link-local (fe80::/10)
            if (address.IsIPv6LinkLocal) return true;

            // IPv6 ULA (fc00::/7)
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;

            // IPv6 loopback (::1)
            if (IPAddress.IsLoopback(address)) return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // Unsubscribe from local discovery events
            if (_localDiscovery != null)
            {
                _localDiscovery.DeviceDiscovered -= OnLocalDeviceDiscovered;
            }

            // Unsubscribe from global discovery events
            if (_globalDiscovery != null)
            {
                _globalDiscovery.DeviceDiscovered -= OnGlobalDeviceDiscovered;
            }

            _lookupSemaphore.Dispose();
            _disposed = true;
        }
    }
}
