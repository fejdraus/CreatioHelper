using System.Collections.Concurrent;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;

using SyncEventType = CreatioHelper.Domain.Entities.Events.SyncEventType;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Implementation of pending device and folder management.
/// Based on Syncthing's handling in lib/model/model.go (handlePendingDevice, handlePendingFolder)
/// </summary>
public class PendingManager : IPendingManager
{
    private readonly ILogger<PendingManager> _logger;
    private readonly IDeviceInfoRepository _deviceRepository;
    private readonly IFolderConfigRepository _folderRepository;
    private readonly IEventLogger _eventLogger;

    private readonly ConcurrentDictionary<string, PendingDevice> _pendingDevices = new();
    private readonly ConcurrentDictionary<string, PendingFolder> _pendingFolders = new();
    private readonly object _statsLock = new();

    private DateTime? _lastPendingAddition;
    private PendingManagerStatistics _statistics = new();

    public PendingManager(
        ILogger<PendingManager> logger,
        IDeviceInfoRepository deviceRepository,
        IFolderConfigRepository folderRepository,
        IEventLogger eventLogger)
    {
        _logger = logger;
        _deviceRepository = deviceRepository;
        _folderRepository = folderRepository;
        _eventLogger = eventLogger;
    }

    /// <inheritdoc />
    public Task AddPendingDeviceAsync(PendingDevice device, CancellationToken cancellationToken = default)
    {
        var key = device.DeviceId;

        _pendingDevices.AddOrUpdate(key, device, (k, existing) =>
        {
            // Update last seen and connection attempts
            existing.LastSeen = DateTime.UtcNow;
            existing.ConnectionAttempts++;
            if (!string.IsNullOrEmpty(device.Address))
                existing.Address = device.Address;
            return existing;
        });

        lock (_statsLock)
        {
            _lastPendingAddition = DateTime.UtcNow;
        }

        _logger.LogInformation("Added pending device {DeviceId} ({DeviceName}) from {Address}",
            device.DeviceId, device.DeviceName, device.Address);

        // Emit event
        _eventLogger.LogEvent(
            SyncEventType.PendingDevicesChanged,
            new
            {
                Action = "added",
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                Address = device.Address
            },
            $"New pending device: {device.DeviceName}",
            device.DeviceId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddPendingFolderAsync(PendingFolder folder, CancellationToken cancellationToken = default)
    {
        // Key is folderId:deviceId to track per-device offers
        var key = $"{folder.FolderId}:{folder.OfferedByDeviceId}";

        _pendingFolders.AddOrUpdate(key, folder, (k, existing) =>
        {
            existing.LastSeen = DateTime.UtcNow;
            return existing;
        });

        lock (_statsLock)
        {
            _lastPendingAddition = DateTime.UtcNow;
        }

        _logger.LogInformation("Added pending folder {FolderId} ({FolderLabel}) offered by {DeviceId}",
            folder.FolderId, folder.FolderLabel, folder.OfferedByDeviceId);

        // Emit event
        _eventLogger.LogEvent(
            SyncEventType.PendingFoldersChanged,
            new
            {
                Action = "added",
                FolderId = folder.FolderId,
                FolderLabel = folder.FolderLabel,
                OfferedBy = folder.OfferedByDeviceId
            },
            $"New pending folder: {folder.FolderLabel}",
            folderId: folder.FolderId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SyncDevice> AcceptDeviceAsync(string deviceId, string? customName = null, CancellationToken cancellationToken = default)
    {
        if (!_pendingDevices.TryRemove(deviceId, out var pending))
        {
            throw new InvalidOperationException($"Device {deviceId} is not pending");
        }

        var device = new SyncDevice(deviceId, customName ?? pending.DeviceName);
        if (!string.IsNullOrEmpty(pending.Address))
        {
            device.AddAddress(pending.Address);
        }

        await _deviceRepository.UpsertAsync(device);

        lock (_statsLock)
        {
            _statistics.TotalDevicesAccepted++;
        }

        _logger.LogInformation("Accepted pending device {DeviceId} ({DeviceName})",
            deviceId, device.DeviceName);

        // Emit events
        _eventLogger.LogEvent(
            SyncEventType.DeviceDiscovered,
            new { DeviceId = deviceId, DeviceName = device.DeviceName, WasPending = true },
            $"Accepted device: {device.DeviceName}",
            deviceId);

        _eventLogger.LogEvent(
            SyncEventType.PendingDevicesChanged,
            new { Action = "accepted", DeviceId = deviceId },
            $"Pending device accepted: {device.DeviceName}",
            deviceId);

        return device;
    }

    /// <inheritdoc />
    public async Task<SyncFolder> AcceptFolderAsync(string folderId, string localPath, string? customLabel = null, CancellationToken cancellationToken = default)
    {
        // Find the pending folder (could be offered by multiple devices)
        var pendingEntry = _pendingFolders.FirstOrDefault(kv => kv.Value.FolderId == folderId && !kv.Value.IsRejected);
        if (pendingEntry.Value == null)
        {
            throw new InvalidOperationException($"Folder {folderId} is not pending");
        }

        var pending = pendingEntry.Value;
        var label = customLabel ?? pending.FolderLabel;
        var folderType = pending.ReceiveEncrypted ? "receiveencrypted" : "sendreceive";

        var folder = new SyncFolder(folderId, label, localPath, folderType);

        // Add the device that offered the folder
        folder.AddDevice(pending.OfferedByDeviceId);

        await _folderRepository.UpsertAsync(folder);

        // Remove all pending entries for this folder
        var keysToRemove = _pendingFolders
            .Where(kv => kv.Value.FolderId == folderId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _pendingFolders.TryRemove(key, out _);
        }

        lock (_statsLock)
        {
            _statistics.TotalFoldersAccepted++;
        }

        _logger.LogInformation("Accepted pending folder {FolderId} ({Label}) to path {Path}",
            folderId, label, localPath);

        // Emit event
        _eventLogger.LogEvent(
            SyncEventType.PendingFoldersChanged,
            new { Action = "accepted", FolderId = folderId, Path = localPath },
            $"Pending folder accepted: {label}",
            folderId: folderId);

        return folder;
    }

    /// <inheritdoc />
    public Task RejectDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (_pendingDevices.TryGetValue(deviceId, out var pending))
        {
            pending.IsRejected = true;
        }

        lock (_statsLock)
        {
            _statistics.TotalDevicesRejected++;
        }

        _logger.LogInformation("Rejected pending device {DeviceId}", deviceId);

        // Emit event
        _eventLogger.LogEvent(
            SyncEventType.PendingDevicesChanged,
            new { Action = "rejected", DeviceId = deviceId },
            $"Pending device rejected: {deviceId}",
            deviceId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RejectFolderAsync(string folderId, string offeredByDeviceId, CancellationToken cancellationToken = default)
    {
        var key = $"{folderId}:{offeredByDeviceId}";

        if (_pendingFolders.TryGetValue(key, out var pending))
        {
            pending.IsRejected = true;
        }

        // Also add to device's ignored folders list
        Task.Run(async () =>
        {
            var device = await _deviceRepository.GetAsync(offeredByDeviceId);
            if (device != null && !device.IgnoredFolders.Contains(folderId))
            {
                device.IgnoredFolders.Add(folderId);
                await _deviceRepository.UpsertAsync(device);
            }
        }, cancellationToken);

        lock (_statsLock)
        {
            _statistics.TotalFoldersRejected++;
        }

        _logger.LogInformation("Rejected pending folder {FolderId} from device {DeviceId}", folderId, offeredByDeviceId);

        // Emit event
        _eventLogger.LogEvent(
            SyncEventType.PendingFoldersChanged,
            new { Action = "rejected", FolderId = folderId, OfferedBy = offeredByDeviceId },
            $"Pending folder rejected: {folderId}",
            folderId: folderId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IEnumerable<PendingDevice>> GetPendingDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = _pendingDevices.Values
            .Where(d => !d.IsRejected)
            .OrderByDescending(d => d.LastSeen)
            .ToList();

        return Task.FromResult<IEnumerable<PendingDevice>>(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<PendingFolder>> GetPendingFoldersAsync(CancellationToken cancellationToken = default)
    {
        var result = _pendingFolders.Values
            .Where(f => !f.IsRejected)
            .OrderByDescending(f => f.LastSeen)
            .ToList();

        return Task.FromResult<IEnumerable<PendingFolder>>(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<PendingFolder>> GetPendingFoldersForDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var result = _pendingFolders.Values
            .Where(f => f.OfferedByDeviceId == deviceId && !f.IsRejected)
            .OrderByDescending(f => f.LastSeen)
            .ToList();

        return Task.FromResult<IEnumerable<PendingFolder>>(result);
    }

    /// <inheritdoc />
    public Task<bool> IsDevicePendingAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_pendingDevices.TryGetValue(deviceId, out var pending) && !pending.IsRejected);
    }

    /// <inheritdoc />
    public Task<bool> IsFolderPendingAsync(string folderId, string offeredByDeviceId, CancellationToken cancellationToken = default)
    {
        var key = $"{folderId}:{offeredByDeviceId}";
        return Task.FromResult(_pendingFolders.TryGetValue(key, out var pending) && !pending.IsRejected);
    }

    /// <inheritdoc />
    public Task CleanupStalePendingItemsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - maxAge;

        // Clean stale devices
        var staleDeviceKeys = _pendingDevices
            .Where(kv => kv.Value.LastSeen < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in staleDeviceKeys)
        {
            if (_pendingDevices.TryRemove(key, out var removed))
            {
                _logger.LogDebug("Removed stale pending device {DeviceId}", removed.DeviceId);
            }
        }

        // Clean stale folders
        var staleFolderKeys = _pendingFolders
            .Where(kv => kv.Value.LastSeen < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in staleFolderKeys)
        {
            if (_pendingFolders.TryRemove(key, out var removed))
            {
                _logger.LogDebug("Removed stale pending folder {FolderId}", removed.FolderId);
            }
        }

        if (staleDeviceKeys.Count > 0 || staleFolderKeys.Count > 0)
        {
            _logger.LogInformation("Cleaned up {DeviceCount} stale pending devices and {FolderCount} stale pending folders",
                staleDeviceKeys.Count, staleFolderKeys.Count);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAllPendingAsync(CancellationToken cancellationToken = default)
    {
        _pendingDevices.Clear();
        _pendingFolders.Clear();

        _logger.LogInformation("Cleared all pending devices and folders");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public PendingManagerStatus GetStatus()
    {
        return new PendingManagerStatus
        {
            PendingDeviceCount = _pendingDevices.Values.Count(d => !d.IsRejected),
            PendingFolderCount = _pendingFolders.Values.Count(f => !f.IsRejected),
            RejectedDeviceCount = _pendingDevices.Values.Count(d => d.IsRejected),
            RejectedFolderCount = _pendingFolders.Values.Count(f => f.IsRejected),
            LastPendingAddition = _lastPendingAddition,
            Statistics = new PendingManagerStatistics
            {
                TotalDevicesAccepted = _statistics.TotalDevicesAccepted,
                TotalDevicesRejected = _statistics.TotalDevicesRejected,
                TotalFoldersAccepted = _statistics.TotalFoldersAccepted,
                TotalFoldersRejected = _statistics.TotalFoldersRejected
            }
        };
    }
}
