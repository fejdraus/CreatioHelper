using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;

/// <summary>
/// Service for managing pending folders and devices awaiting user approval.
/// Based on Syncthing's pending folders/devices functionality.
/// </summary>
public interface IPendingService
{
    // Pending Devices

    /// <summary>
    /// Add a pending device (discovered but not yet approved).
    /// </summary>
    void AddPendingDevice(PendingDevice device);

    /// <summary>
    /// Get all pending devices.
    /// </summary>
    IReadOnlyList<PendingDevice> GetPendingDevices();

    /// <summary>
    /// Get a specific pending device.
    /// </summary>
    PendingDevice? GetPendingDevice(string deviceId);

    /// <summary>
    /// Approve a pending device.
    /// </summary>
    bool ApprovePendingDevice(string deviceId);

    /// <summary>
    /// Reject/ignore a pending device.
    /// </summary>
    bool RejectPendingDevice(string deviceId);

    /// <summary>
    /// Check if a device is pending.
    /// </summary>
    bool IsDevicePending(string deviceId);

    // Pending Folders

    /// <summary>
    /// Add a pending folder (offered by a device but not yet accepted).
    /// </summary>
    void AddPendingFolder(PendingFolder folder);

    /// <summary>
    /// Get all pending folders.
    /// </summary>
    IReadOnlyList<PendingFolder> GetPendingFolders();

    /// <summary>
    /// Get pending folders from a specific device.
    /// </summary>
    IReadOnlyList<PendingFolder> GetPendingFoldersFromDevice(string deviceId);

    /// <summary>
    /// Get a specific pending folder.
    /// </summary>
    PendingFolder? GetPendingFolder(string folderId, string offeredByDeviceId);

    /// <summary>
    /// Accept a pending folder.
    /// </summary>
    bool AcceptPendingFolder(string folderId, string offeredByDeviceId, string? localPath = null);

    /// <summary>
    /// Reject/ignore a pending folder.
    /// </summary>
    bool RejectPendingFolder(string folderId, string offeredByDeviceId);

    /// <summary>
    /// Check if a folder is pending from a device.
    /// </summary>
    bool IsFolderPending(string folderId, string offeredByDeviceId);

    // Events

    /// <summary>
    /// Event raised when a new device is pending.
    /// </summary>
    event EventHandler<PendingDeviceEventArgs>? DevicePending;

    /// <summary>
    /// Event raised when a new folder is pending.
    /// </summary>
    event EventHandler<PendingFolderEventArgs>? FolderPending;

    /// <summary>
    /// Event raised when a pending device is approved.
    /// </summary>
    event EventHandler<PendingDeviceEventArgs>? DeviceApproved;

    /// <summary>
    /// Event raised when a pending folder is accepted.
    /// </summary>
    event EventHandler<PendingFolderEventArgs>? FolderAccepted;
}

/// <summary>
/// Represents a pending device awaiting approval.
/// </summary>
public class PendingDevice
{
    public string DeviceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Address { get; init; }
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;
    public string? IntroducedBy { get; init; }
    public int ConnectionAttempts { get; set; }
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>
/// Represents a pending folder offered by a device.
/// </summary>
public class PendingFolder
{
    public string FolderId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string OfferedByDeviceId { get; init; } = string.Empty;
    public string? OfferedByDeviceName { get; init; }
    public DateTime OfferedAt { get; init; } = DateTime.UtcNow;
    public bool ReceiveEncrypted { get; init; }
    public bool IsEncrypted { get; init; }
}

/// <summary>
/// Event args for pending device events.
/// </summary>
public class PendingDeviceEventArgs : EventArgs
{
    public PendingDevice Device { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Event args for pending folder events.
/// </summary>
public class PendingFolderEventArgs : EventArgs
{
    public PendingFolder Folder { get; init; } = new();
    public string? AcceptedLocalPath { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for pending service.
/// </summary>
public class PendingServiceConfiguration
{
    /// <summary>
    /// Auto-accept devices introduced by trusted introducers.
    /// </summary>
    public bool AutoAcceptIntroducedDevices { get; set; } = false;

    /// <summary>
    /// Auto-accept folders from trusted devices.
    /// </summary>
    public bool AutoAcceptFolders { get; set; } = false;

    /// <summary>
    /// Device IDs that are trusted introducers.
    /// </summary>
    public HashSet<string> TrustedIntroducers { get; } = new();

    /// <summary>
    /// Device IDs from which to auto-accept folders.
    /// </summary>
    public HashSet<string> AutoAcceptFromDevices { get; } = new();

    /// <summary>
    /// Maximum time to keep pending items before expiring.
    /// </summary>
    public TimeSpan PendingExpiration { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Rejected device IDs (ignored permanently).
    /// </summary>
    public HashSet<string> RejectedDevices { get; } = new();

    /// <summary>
    /// Rejected folder keys (folderId:deviceId).
    /// </summary>
    public HashSet<string> RejectedFolders { get; } = new();
}

/// <summary>
/// Implementation of pending service.
/// </summary>
public class PendingService : IPendingService
{
    private readonly ILogger<PendingService> _logger;
    private readonly PendingServiceConfiguration _config;
    private readonly ConcurrentDictionary<string, PendingDevice> _pendingDevices = new();
    private readonly ConcurrentDictionary<string, PendingFolder> _pendingFolders = new();

    public event EventHandler<PendingDeviceEventArgs>? DevicePending;
    public event EventHandler<PendingDeviceEventArgs>? DeviceApproved;
    public event EventHandler<PendingFolderEventArgs>? FolderPending;
    public event EventHandler<PendingFolderEventArgs>? FolderAccepted;

    public PendingService(
        ILogger<PendingService> logger,
        PendingServiceConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new PendingServiceConfiguration();
    }

    #region Pending Devices

    /// <inheritdoc />
    public void AddPendingDevice(PendingDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (string.IsNullOrEmpty(device.DeviceId))
        {
            throw new ArgumentException("Device ID cannot be empty", nameof(device));
        }

        // Check if already rejected
        if (_config.RejectedDevices.Contains(device.DeviceId))
        {
            _logger.LogDebug("Ignoring rejected device {DeviceId}", device.DeviceId);
            return;
        }

        // Check for auto-accept via introducer
        if (_config.AutoAcceptIntroducedDevices &&
            !string.IsNullOrEmpty(device.IntroducedBy) &&
            _config.TrustedIntroducers.Contains(device.IntroducedBy))
        {
            _logger.LogInformation("Auto-accepting device {DeviceId} introduced by {Introducer}",
                device.DeviceId, device.IntroducedBy);
            DeviceApproved?.Invoke(this, new PendingDeviceEventArgs { Device = device });
            return;
        }

        _pendingDevices[device.DeviceId] = device;
        _logger.LogInformation("Added pending device {DeviceId} ({Name})", device.DeviceId, device.Name);

        DevicePending?.Invoke(this, new PendingDeviceEventArgs { Device = device });
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingDevice> GetPendingDevices()
    {
        CleanupExpired();
        return _pendingDevices.Values.ToList();
    }

    /// <inheritdoc />
    public PendingDevice? GetPendingDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        _pendingDevices.TryGetValue(deviceId, out var device);
        return device;
    }

    /// <inheritdoc />
    public bool ApprovePendingDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (_pendingDevices.TryRemove(deviceId, out var device))
        {
            _logger.LogInformation("Approved pending device {DeviceId}", deviceId);
            DeviceApproved?.Invoke(this, new PendingDeviceEventArgs { Device = device });
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool RejectPendingDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (_pendingDevices.TryRemove(deviceId, out var device))
        {
            _config.RejectedDevices.Add(deviceId);
            _logger.LogInformation("Rejected pending device {DeviceId}", deviceId);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsDevicePending(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return _pendingDevices.ContainsKey(deviceId);
    }

    #endregion

    #region Pending Folders

    /// <inheritdoc />
    public void AddPendingFolder(PendingFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (string.IsNullOrEmpty(folder.FolderId))
        {
            throw new ArgumentException("Folder ID cannot be empty", nameof(folder));
        }

        var key = GetFolderKey(folder.FolderId, folder.OfferedByDeviceId);

        // Check if already rejected
        if (_config.RejectedFolders.Contains(key))
        {
            _logger.LogDebug("Ignoring rejected folder {FolderId} from {DeviceId}",
                folder.FolderId, folder.OfferedByDeviceId);
            return;
        }

        // Check for auto-accept
        if (_config.AutoAcceptFolders &&
            _config.AutoAcceptFromDevices.Contains(folder.OfferedByDeviceId))
        {
            _logger.LogInformation("Auto-accepting folder {FolderId} from {DeviceId}",
                folder.FolderId, folder.OfferedByDeviceId);
            FolderAccepted?.Invoke(this, new PendingFolderEventArgs { Folder = folder });
            return;
        }

        _pendingFolders[key] = folder;
        _logger.LogInformation("Added pending folder {FolderId} ({Label}) from {DeviceId}",
            folder.FolderId, folder.Label, folder.OfferedByDeviceId);

        FolderPending?.Invoke(this, new PendingFolderEventArgs { Folder = folder });
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingFolder> GetPendingFolders()
    {
        CleanupExpired();
        return _pendingFolders.Values.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingFolder> GetPendingFoldersFromDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        return _pendingFolders.Values
            .Where(f => f.OfferedByDeviceId == deviceId)
            .ToList();
    }

    /// <inheritdoc />
    public PendingFolder? GetPendingFolder(string folderId, string offeredByDeviceId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(offeredByDeviceId);

        var key = GetFolderKey(folderId, offeredByDeviceId);
        _pendingFolders.TryGetValue(key, out var folder);
        return folder;
    }

    /// <inheritdoc />
    public bool AcceptPendingFolder(string folderId, string offeredByDeviceId, string? localPath = null)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(offeredByDeviceId);

        var key = GetFolderKey(folderId, offeredByDeviceId);

        if (_pendingFolders.TryRemove(key, out var folder))
        {
            _logger.LogInformation("Accepted pending folder {FolderId} from {DeviceId}",
                folderId, offeredByDeviceId);
            FolderAccepted?.Invoke(this, new PendingFolderEventArgs
            {
                Folder = folder,
                AcceptedLocalPath = localPath
            });
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool RejectPendingFolder(string folderId, string offeredByDeviceId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(offeredByDeviceId);

        var key = GetFolderKey(folderId, offeredByDeviceId);

        if (_pendingFolders.TryRemove(key, out _))
        {
            _config.RejectedFolders.Add(key);
            _logger.LogInformation("Rejected pending folder {FolderId} from {DeviceId}",
                folderId, offeredByDeviceId);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsFolderPending(string folderId, string offeredByDeviceId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(offeredByDeviceId);

        var key = GetFolderKey(folderId, offeredByDeviceId);
        return _pendingFolders.ContainsKey(key);
    }

    #endregion

    private static string GetFolderKey(string folderId, string deviceId) => $"{folderId}:{deviceId}";

    private void CleanupExpired()
    {
        var cutoff = DateTime.UtcNow - _config.PendingExpiration;

        foreach (var kvp in _pendingDevices)
        {
            if (kvp.Value.DiscoveredAt < cutoff)
            {
                _pendingDevices.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _pendingFolders)
        {
            if (kvp.Value.OfferedAt < cutoff)
            {
                _pendingFolders.TryRemove(kvp.Key, out _);
            }
        }
    }
}
