using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Device discovery interface (based on Syncthing discovery)
/// </summary>
public interface IDeviceDiscovery : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task AnnounceAsync(SyncDevice localDevice, List<string> addresses);
    Task<List<DiscoveredDevice>> DiscoverAsync(string deviceId, CancellationToken cancellationToken = default);
    Task SetGlobalDiscoveryServersAsync(List<string> servers);
    Task SetLocalDiscoveryPortAsync(int port);
    event EventHandler<DeviceDiscoveredEventArgs> DeviceDiscovered;
}

public class DiscoveredDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public DiscoverySource Source { get; set; } = DiscoverySource.Local;
}

public class DeviceDiscoveredEventArgs : EventArgs
{
    public DiscoveredDevice Device { get; }
    public DeviceDiscoveredEventArgs(DiscoveredDevice device) => Device = device;
}

public enum DiscoverySource
{
    Local,
    Global,
    Static
}