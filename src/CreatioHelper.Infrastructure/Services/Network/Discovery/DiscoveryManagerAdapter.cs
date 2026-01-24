using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using AppDiscoveredDevice = CreatioHelper.Application.Interfaces.DiscoveredDevice;

namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// Adapter that makes IDiscoveryManager work with the legacy IDeviceDiscovery interface.
/// This allows gradual migration from the old discovery system to the new DiscoveryManager.
/// </summary>
public class DiscoveryManagerAdapter : IDeviceDiscovery
{
    private readonly ILogger<DiscoveryManagerAdapter> _logger;
    private readonly IDiscoveryManager _discoveryManager;
    private bool _disposed;

    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    public DiscoveryManagerAdapter(ILogger<DiscoveryManagerAdapter> logger, IDiscoveryManager discoveryManager)
    {
        _logger = logger;
        _discoveryManager = discoveryManager;

        // Subscribe to discovery manager events and forward them
        _discoveryManager.DeviceDiscovered += OnDeviceDiscovered;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _discoveryManager.StartAsync(cancellationToken);
    }

    public Task StopAsync()
    {
        return _discoveryManager.StopAsync();
    }

    public async Task AnnounceAsync(SyncDevice localDevice, List<string> addresses)
    {
        // The DiscoveryManager handles announcements internally
        // For static addresses, we can add them
        if (addresses.Count > 0)
        {
            _discoveryManager.AddStaticAddresses(localDevice.DeviceId, addresses);
        }
    }

    public async Task<List<AppDiscoveredDevice>> DiscoverAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var result = await _discoveryManager.LookupAsync(deviceId, cancellationToken);

        if (!result.Found)
        {
            return new List<AppDiscoveredDevice>();
        }

        // Convert DiscoveryResult to DiscoveredDevice list
        return new List<AppDiscoveredDevice>
        {
            new AppDiscoveredDevice
            {
                DeviceId = result.DeviceId,
                Addresses = result.GetAddressStrings().ToList(),
                LastSeen = DateTime.UtcNow,
                Source = ConvertSource(result.Sources.FirstOrDefault())
            }
        };
    }

    public Task SetGlobalDiscoveryServersAsync(List<string> servers)
    {
        // DiscoveryManager handles this internally through configuration
        _logger.LogDebug("SetGlobalDiscoveryServersAsync called with {Count} servers", servers.Count);
        return Task.CompletedTask;
    }

    public Task SetLocalDiscoveryPortAsync(int port)
    {
        // DiscoveryManager handles this internally through configuration
        _logger.LogDebug("SetLocalDiscoveryPortAsync called with port {Port}", port);
        return Task.CompletedTask;
    }

    private void OnDeviceDiscovered(object? sender, DeviceDiscoveredArgs e)
    {
        var discovered = new AppDiscoveredDevice
        {
            DeviceId = e.DeviceId,
            Addresses = e.Addresses.Select(a => a.Address).ToList(),
            LastSeen = DateTime.UtcNow,
            Source = ConvertSource(e.Source)
        };

        DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(discovered));
    }

    private static DiscoverySource ConvertSource(DiscoveryCacheSource source)
    {
        return source switch
        {
            DiscoveryCacheSource.Local => DiscoverySource.Local,
            DiscoveryCacheSource.Global => DiscoverySource.Global,
            DiscoveryCacheSource.Static => DiscoverySource.Static,
            _ => DiscoverySource.Local
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _discoveryManager.DeviceDiscovered -= OnDeviceDiscovered;
            // Don't dispose _discoveryManager as it's injected
            _disposed = true;
        }
    }
}
