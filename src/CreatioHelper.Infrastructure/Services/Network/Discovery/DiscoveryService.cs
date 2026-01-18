using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// Combined discovery service managing both local and global device discovery.
/// Compatible with Syncthing's discovery mechanisms.
/// </summary>
public class DiscoveryService : IAsyncDisposable
{
    private readonly ILogger<DiscoveryService> _logger;
    private readonly LocalDiscovery _localDiscovery;
    private readonly GlobalDiscovery _globalDiscovery;
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _knownDevices;
    private readonly DiscoveryOptions _options;

    private CancellationTokenSource? _cancellationTokenSource;
    private List<string> _ourAddresses = new();

    /// <summary>
    /// Event fired when a device is discovered.
    /// </summary>
    public event Func<DiscoveredDevice, Task>? DeviceDiscovered;

    /// <summary>
    /// Event fired when a device is lost (no longer responds).
    /// </summary>
    public event Func<string, Task>? DeviceLost;

    public DiscoveryService(
        ILogger<DiscoveryService> logger,
        ILoggerFactory loggerFactory,
        string deviceId,
        DiscoveryOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new DiscoveryOptions();
        _knownDevices = new ConcurrentDictionary<string, DiscoveredDevice>();

        _localDiscovery = new LocalDiscovery(
            loggerFactory.CreateLogger<LocalDiscovery>(),
            deviceId,
            _options.LocalDiscoveryPort,
            _options.LocalAnnounceIntervalSeconds);

        _globalDiscovery = new GlobalDiscovery(
            loggerFactory.CreateLogger<GlobalDiscovery>(),
            deviceId,
            _options.GlobalDiscoveryServers,
            _options.GlobalAnnounceIntervalSeconds,
            _options.LookupCacheSeconds);

        _localDiscovery.DeviceDiscovered += OnDeviceDiscoveredAsync;
        _globalDiscovery.DeviceDiscovered += OnDeviceDiscoveredAsync;
    }

    /// <summary>
    /// Gets whether discovery is running.
    /// </summary>
    public bool IsRunning => _cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested;

    /// <summary>
    /// Gets all known devices.
    /// </summary>
    public IReadOnlyDictionary<string, DiscoveredDevice> KnownDevices => _knownDevices;

    /// <summary>
    /// Starts discovery services.
    /// </summary>
    public async Task StartAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            _logger.LogWarning("Discovery service is already running");
            return;
        }

        _ourAddresses = addresses.ToList();
        _cancellationTokenSource = new CancellationTokenSource();

        var tasks = new List<Task>();

        if (_options.EnableLocalDiscovery)
        {
            tasks.Add(_localDiscovery.StartAsync(_ourAddresses, cancellationToken));
        }

        if (_options.EnableGlobalDiscovery)
        {
            tasks.Add(_globalDiscovery.StartAsync(_ourAddresses, cancellationToken));
        }

        await Task.WhenAll(tasks);

        _logger.LogInformation("Discovery service started (Local={Local}, Global={Global})",
            _options.EnableLocalDiscovery, _options.EnableGlobalDiscovery);
    }

    /// <summary>
    /// Stops discovery services.
    /// </summary>
    public async Task StopAsync()
    {
        _cancellationTokenSource?.Cancel();

        var tasks = new List<Task>
        {
            _localDiscovery.StopAsync(),
            _globalDiscovery.StopAsync()
        };

        await Task.WhenAll(tasks);

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _logger.LogInformation("Discovery service stopped");
    }

    /// <summary>
    /// Looks up a device by ID.
    /// </summary>
    public async Task<DiscoveredDevice?> LookupDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        // Check known devices first
        if (_knownDevices.TryGetValue(deviceId, out var known))
        {
            var age = DateTime.UtcNow - known.DiscoveredAt;
            if (age < TimeSpan.FromSeconds(_options.LookupCacheSeconds))
            {
                return known;
            }
        }

        // Try global discovery lookup
        if (_options.EnableGlobalDiscovery)
        {
            var discovered = await _globalDiscovery.LookupAsync(deviceId, cancellationToken);
            if (discovered != null)
            {
                _knownDevices[deviceId] = discovered;
                return discovered;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds a static device address (for manually configured devices).
    /// </summary>
    public void AddStaticDevice(string deviceId, IEnumerable<string> addresses)
    {
        var device = new DiscoveredDevice
        {
            DeviceId = deviceId,
            Addresses = addresses.ToList(),
            DiscoveredAt = DateTime.UtcNow,
            DiscoveryMethod = DiscoveryMethod.Static
        };

        _knownDevices[deviceId] = device;
        _logger.LogDebug("Added static device: {DeviceId} with {Count} addresses",
            deviceId, device.Addresses.Count);
    }

    /// <summary>
    /// Removes a device from known devices.
    /// </summary>
    public void RemoveDevice(string deviceId)
    {
        if (_knownDevices.TryRemove(deviceId, out _))
        {
            _globalDiscovery.InvalidateCache(deviceId);
            _logger.LogDebug("Removed device: {DeviceId}", deviceId);
        }
    }

    /// <summary>
    /// Updates our announced addresses.
    /// </summary>
    public async Task UpdateAddressesAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        _ourAddresses = addresses.ToList();

        var tasks = new List<Task>();

        if (_options.EnableLocalDiscovery)
        {
            tasks.Add(_localDiscovery.AnnounceAsync(_ourAddresses, cancellationToken));
        }

        if (_options.EnableGlobalDiscovery)
        {
            tasks.Add(_globalDiscovery.AnnounceAsync(_ourAddresses, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Gets the best addresses to connect to a device.
    /// Prioritizes addresses based on discovery method and reachability.
    /// </summary>
    public List<string> GetBestAddresses(string deviceId)
    {
        if (!_knownDevices.TryGetValue(deviceId, out var device))
        {
            return new List<string>();
        }

        // Sort addresses by preference:
        // 1. Local addresses (192.168.x.x, 10.x.x.x, etc.)
        // 2. IPv4 addresses
        // 3. IPv6 addresses
        // 4. Relay addresses
        return device.Addresses
            .OrderBy(a => GetAddressPriority(a))
            .ToList();
    }

    private static int GetAddressPriority(string address)
    {
        if (address.StartsWith("tcp://192.168.") ||
            address.StartsWith("tcp://10.") ||
            address.StartsWith("tcp://172."))
        {
            return 0; // Local network
        }
        if (address.StartsWith("quic://"))
        {
            return 1; // QUIC
        }
        if (address.StartsWith("tcp://"))
        {
            return 2; // TCP
        }
        if (address.StartsWith("relay://"))
        {
            return 100; // Relay (fallback)
        }
        return 50; // Unknown
    }

    private async Task OnDeviceDiscoveredAsync(DiscoveredDevice device)
    {
        // Update or add device
        _knownDevices.AddOrUpdate(
            device.DeviceId,
            device,
            (_, existing) =>
            {
                // Merge addresses
                var mergedAddresses = existing.Addresses
                    .Union(device.Addresses)
                    .Distinct()
                    .ToList();

                return new DiscoveredDevice
                {
                    DeviceId = device.DeviceId,
                    Addresses = mergedAddresses,
                    DiscoveredAt = DateTime.UtcNow,
                    DiscoveryMethod = device.DiscoveryMethod,
                    SourceEndpoint = device.SourceEndpoint,
                    InstanceId = device.InstanceId
                };
            });

        _logger.LogDebug("Device discovered/updated: {DeviceId} via {Method}",
            device.DeviceId, device.DiscoveryMethod);

        if (DeviceDiscovered != null)
        {
            await DeviceDiscovered.Invoke(device);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _localDiscovery.DisposeAsync();
        await _globalDiscovery.DisposeAsync();
    }
}

/// <summary>
/// Configuration options for discovery services.
/// </summary>
public class DiscoveryOptions
{
    /// <summary>
    /// Whether to enable local discovery.
    /// </summary>
    public bool EnableLocalDiscovery { get; set; } = true;

    /// <summary>
    /// Whether to enable global discovery.
    /// </summary>
    public bool EnableGlobalDiscovery { get; set; } = true;

    /// <summary>
    /// Port for local discovery UDP broadcasts.
    /// </summary>
    public int LocalDiscoveryPort { get; set; } = 21027;

    /// <summary>
    /// Interval in seconds between local discovery announcements.
    /// </summary>
    public int LocalAnnounceIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Interval in seconds between global discovery announcements.
    /// </summary>
    public int GlobalAnnounceIntervalSeconds { get; set; } = 1800;

    /// <summary>
    /// Cache duration for device lookups in seconds.
    /// </summary>
    public int LookupCacheSeconds { get; set; } = 300;

    /// <summary>
    /// List of global discovery server URLs.
    /// If null, uses default Syncthing discovery servers.
    /// </summary>
    public List<string>? GlobalDiscoveryServers { get; set; }
}
