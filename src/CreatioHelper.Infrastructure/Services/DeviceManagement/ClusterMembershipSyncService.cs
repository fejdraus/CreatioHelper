using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Configuration;
using CreatioHelper.Infrastructure.Services.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

public class ClusterMembershipSyncService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);

    private readonly IClusterMembershipService _membership;
    private readonly ISyncEngine _syncEngine;
    private readonly CreatioHelper.Application.Interfaces.IConfigurationManager _configManager;
    private readonly IConfigurationStore _store;
    private readonly ClusterMembershipRegistry _registry;
    private readonly ClusterKeyProvider _keyProvider;
    private readonly ClusterKeyConfiguration _keyConfig;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClusterMembershipSyncService> _logger;

    public ClusterMembershipSyncService(
        IClusterMembershipService membership,
        ISyncEngine syncEngine,
        CreatioHelper.Application.Interfaces.IConfigurationManager configManager,
        IConfigurationStore store,
        ClusterMembershipRegistry registry,
        ClusterKeyProvider keyProvider,
        ClusterKeyConfiguration keyConfig,
        IConfiguration configuration,
        ILogger<ClusterMembershipSyncService> logger)
    {
        _membership = membership;
        _syncEngine = syncEngine;
        _configManager = configManager;
        _store = store;
        _registry = registry;
        _keyProvider = keyProvider;
        _keyConfig = keyConfig;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ClusterModeSettings.IsClusterMode(_configuration))
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!await ResolveClusterKeyAsync())
        {
            return;
        }

        await RegisterSelfAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cluster membership reconcile failed");
            }

            try
            {
                await Task.Delay(ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> ResolveClusterKeyAsync()
    {
        var localKey = _keyConfig.Key;
        var dbKey = await _store.GetClusterKeyAsync();

        if (string.IsNullOrWhiteSpace(dbKey))
        {
            if (string.IsNullOrWhiteSpace(localKey))
            {
                _logger.LogError(
                    "Cluster mode is enabled but no cluster key is configured and none exists in the shared database; cannot join");
                return false;
            }
            await _store.SetClusterKeyAsync(localKey);
            _keyProvider.Set(localKey);
            _logger.LogInformation("Seeded cluster key into the shared database");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(localKey) &&
            !string.Equals(localKey, dbKey, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Configured cluster key does not match the shared database; refusing to join");
            return false;
        }

        _keyProvider.Set(dbKey);
        _logger.LogInformation("Loaded cluster key from the shared database");
        return true;
    }

    private async Task RegisterSelfAsync(CancellationToken cancellationToken)
    {
        var local = _membership.GetLocalMember();
        var existing = await _configManager.GetDeviceAsync(local.DeviceId);
        var self = new SyncDevice(local.DeviceId, local.DeviceName)
        {
            Addresses = local.Addresses.ToList(),
            AdmittedAt = existing?.AdmittedAt ?? DateTime.UtcNow
        };
        await _configManager.UpsertDeviceAsync(self);
        _logger.LogInformation(
            "Registered self into shared cluster membership: {DeviceId} ({Name})",
            local.DeviceId, local.DeviceName);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var localId = _syncEngine.DeviceId;
        var members = await _store.GetDevicesAsync();
        var memberIds = new HashSet<string>(
            members.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        _registry.Update(memberIds);

        var known = await _syncEngine.GetDevicesAsync();

        if (memberIds.Count > 0 && !memberIds.Contains(localId))
        {
            _logger.LogWarning(
                "This node is no longer a member of the shared cluster database, disconnecting from all peers");
            foreach (var device in known)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(device.DeviceId, localId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                await _syncEngine.DisconnectPeerAsync(device.DeviceId);
            }
            return;
        }

        var connected = new HashSet<string>(
            known.Where(d => d.IsConnected).Select(d => d.DeviceId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(member.Id, localId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (connected.Contains(member.Id))
            {
                continue;
            }
            _logger.LogInformation(
                "Connecting cluster member from shared database: {DeviceId} ({Name})",
                member.Id, member.Name);
            await _syncEngine.ConnectPeerAsync(
                member.Id, member.Name, member.Addresses.ToList());
        }

        foreach (var device in known)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(device.DeviceId, localId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (memberIds.Contains(device.DeviceId))
            {
                continue;
            }
            _logger.LogInformation(
                "Cluster member removed from shared database, disconnecting: {DeviceId} ({Name})",
                device.DeviceId, device.DeviceName);
            await _syncEngine.DisconnectPeerAsync(device.DeviceId);
        }
    }
}
