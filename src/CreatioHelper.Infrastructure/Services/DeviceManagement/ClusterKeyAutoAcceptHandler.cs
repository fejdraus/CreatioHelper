using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Network.Discovery;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

public class ClusterKeyAutoAcceptHandler : IDisposable
{
    private readonly ILogger<ClusterKeyAutoAcceptHandler> _logger;
    private readonly IClusterMembershipService _membership;
    private readonly ISyncEngine _syncEngine;
    private readonly IDiscoveryManager _discoveryManager;
    private bool _disposed;

    public ClusterKeyAutoAcceptHandler(
        ILogger<ClusterKeyAutoAcceptHandler> logger,
        IClusterMembershipService membership,
        ISyncEngine syncEngine,
        IDiscoveryManager discoveryManager)
    {
        _logger = logger;
        _membership = membership;
        _syncEngine = syncEngine;
        _discoveryManager = discoveryManager;

        _discoveryManager.DeviceDiscovered += OnDeviceDiscovered;
    }

    private async void OnDeviceDiscovered(object? sender, DeviceDiscoveredArgs e)
    {
        try
        {
            if (!_membership.IsEnabled)
            {
                return;
            }

            var devices = await _syncEngine.GetDevicesAsync();
            if (devices.Any(d => string.Equals(d.DeviceId, e.DeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _logger.LogInformation("New device {DeviceId} discovered, attempting cluster key auto-pairing", e.DeviceId);

            var apiAddress = _membership.ResolveApiAddress(e.DeviceId, e.Addresses.Select(a => a.Address));
            if (apiAddress == null)
            {
                _logger.LogDebug("No HTTP address found for device {DeviceId}, falling back to pending flow", e.DeviceId);
                return;
            }

            var result = await _membership.PairWithAsync(apiAddress);
            if (!result.Success || result.Remote == null)
            {
                _logger.LogDebug("Cluster key auto-pairing failed for device {DeviceId}: {Error}", e.DeviceId, result.Error);
                return;
            }

            var members = new List<ClusterMember> { result.Remote };
            members.AddRange(result.Roster);

            var added = await _membership.MergeRosterAsync(members);

            _logger.LogInformation(
                "Cluster key auto-pairing with {DeviceId} succeeded, {Added} devices added",
                e.DeviceId, added.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cluster key auto-pairing for device {DeviceId}", e.DeviceId);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _discoveryManager.DeviceDiscovered -= OnDeviceDiscovered;
            _disposed = true;
        }
    }
}
