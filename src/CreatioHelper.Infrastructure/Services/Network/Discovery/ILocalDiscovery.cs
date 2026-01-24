using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// Interface for local network device discovery service.
/// Based on Syncthing's local discovery protocol (UDP multicast/broadcast).
/// </summary>
public interface ILocalDiscovery
{
    /// <summary>
    /// Event raised when a device is discovered on the local network.
    /// </summary>
    event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Start the local discovery service.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the local discovery service.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Discover devices matching the specified device ID on the local network.
    /// </summary>
    /// <param name="deviceId">Device ID to search for, or null to return all discovered devices.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered devices.</returns>
    Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(string? deviceId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Announce this device's presence on the local network.
    /// </summary>
    Task AnnounceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get whether the service is currently running.
    /// </summary>
    bool IsRunning { get; }
}
