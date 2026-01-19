using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;

using SyncEventType = CreatioHelper.Domain.Entities.Events.SyncEventType;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Implementation of device introduction handling.
/// Based on Syncthing's introducer logic from lib/model/model.go handleIntroductions()
///
/// Key behaviors:
/// - Only process introductions from devices marked as introducers
/// - Track which device introduced each new device
/// - Optionally remove introduced devices when introducer is removed
/// - Add introduced devices to folders that introducer shares with us
/// </summary>
public class IntroducerService : IIntroducerService
{
    private readonly ILogger<IntroducerService> _logger;
    private readonly IDeviceInfoRepository _deviceRepository;
    private readonly IFolderConfigRepository _folderRepository;
    private readonly IEventLogger _eventLogger;
    private readonly object _statsLock = new();

    private volatile bool _isRunning = true;
    private DateTime? _lastIntroductionTime;
    private IntroducerStatistics _statistics = new();

    public IntroducerService(
        ILogger<IntroducerService> logger,
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
    public async Task<IntroductionResult> ProcessIntroductionAsync(
        string introducerDeviceId,
        IEnumerable<IntroducedDevice> introducedDevices,
        IEnumerable<IntroducedFolderShare> folderShares,
        CancellationToken cancellationToken = default)
    {
        var result = new IntroductionResult();

        try
        {
            // Verify the introducer device exists and is marked as introducer
            var introducer = await _deviceRepository.GetAsync(introducerDeviceId);
            if (introducer == null)
            {
                result.Errors.Add($"Introducer device {introducerDeviceId} not found");
                return result;
            }

            if (!introducer.Introducer)
            {
                _logger.LogDebug("Device {DeviceId} is not an introducer, ignoring introduction", introducerDeviceId);
                return result;
            }

            _logger.LogInformation("Processing introduction from device {DeviceId} ({DeviceName})",
                introducerDeviceId, introducer.DeviceName);

            // Process each introduced device
            var introducedList = introducedDevices.ToList();
            foreach (var introduced in introducedList)
            {
                if (introduced.DeviceId == introducerDeviceId)
                {
                    // Skip the introducer itself
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await ProcessIntroducedDeviceAsync(introduced, introducerDeviceId, result, cancellationToken);
            }

            // Process folder shares - add introduced devices to appropriate folders
            var folderSharesList = folderShares.ToList();
            foreach (var share in folderSharesList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessFolderShareAsync(share, introducerDeviceId, introducedList, result, cancellationToken);
            }

            // Update statistics
            lock (_statsLock)
            {
                _lastIntroductionTime = DateTime.UtcNow;
                _statistics.TotalIntroductionsProcessed++;
                _statistics.DevicesAdded += result.AddedDevices.Count;
                _statistics.FolderSharesAdded += result.FolderShareChanges.Count(c => c.ChangeType == FolderShareChangeType.Added);
            }

            // Emit event if changes were made
            if (result.ChangesMade)
            {
                _eventLogger.LogEvent(
                    SyncEventType.ConfigSaved,
                    new { IntroducerDeviceId = introducerDeviceId, AddedDevices = result.AddedDevices },
                    $"Processed introduction from {introducer.DeviceName}: added {result.AddedDevices.Count} devices",
                    introducerDeviceId);
            }

            _logger.LogInformation("Introduction from {DeviceId} completed: {Added} added, {Updated} updated",
                introducerDeviceId, result.AddedDevices.Count, result.UpdatedDevices.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Introduction processing cancelled for {DeviceId}", introducerDeviceId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing introduction from {DeviceId}", introducerDeviceId);
            result.Errors.Add(ex.Message);
        }

        return result;
    }

    private async Task ProcessIntroducedDeviceAsync(
        IntroducedDevice introduced,
        string introducerDeviceId,
        IntroductionResult result,
        CancellationToken cancellationToken)
    {
        var existingDevice = await _deviceRepository.GetAsync(introduced.DeviceId);

        if (existingDevice != null)
        {
            // Device already exists - update if necessary
            var needsUpdate = false;

            // Update addresses if there are new ones
            foreach (var addr in introduced.Addresses)
            {
                if (!existingDevice.Addresses.Contains(addr))
                {
                    existingDevice.AddAddress(addr);
                    needsUpdate = true;
                }
            }

            if (needsUpdate)
            {
                await _deviceRepository.UpsertAsync(existingDevice);
                result.UpdatedDevices.Add(introduced.DeviceId);
                result.ChangesMade = true;

                _logger.LogDebug("Updated introduced device {DeviceId} ({DeviceName})",
                    introduced.DeviceId, introduced.DeviceName);
            }
        }
        else
        {
            // Add new device
            var newDevice = new SyncDevice(introduced.DeviceId, introduced.DeviceName)
            {
                Compression = introduced.Compression,
                Introducer = introduced.IsIntroducer,
                MaxRequestKiB = introduced.MaxRequestKiB
            };

            // Set IntroducedBy using reflection since it's a private setter
            var introducedByProp = typeof(SyncDevice).GetProperty(nameof(SyncDevice.IntroducedBy));
            if (introducedByProp != null)
            {
                introducedByProp.SetValue(newDevice, introducerDeviceId);
            }

            // Add addresses
            foreach (var addr in introduced.Addresses)
            {
                newDevice.AddAddress(addr);
            }

            await _deviceRepository.UpsertAsync(newDevice);
            result.AddedDevices.Add(introduced.DeviceId);
            result.ChangesMade = true;

            _logger.LogInformation("Added introduced device {DeviceId} ({DeviceName}) from introducer {IntroducerDeviceId}",
                introduced.DeviceId, introduced.DeviceName, introducerDeviceId);

            // Emit device discovered event
            _eventLogger.LogEvent(
                SyncEventType.DeviceDiscovered,
                new { DeviceId = introduced.DeviceId, DeviceName = introduced.DeviceName, IntroducedBy = introducerDeviceId },
                $"Discovered device {introduced.DeviceName} via introduction",
                introduced.DeviceId);
        }
    }

    private async Task ProcessFolderShareAsync(
        IntroducedFolderShare share,
        string introducerDeviceId,
        List<IntroducedDevice> introducedDevices,
        IntroductionResult result,
        CancellationToken cancellationToken)
    {
        // Check if we have this folder
        var folder = await _folderRepository.GetAsync(share.FolderId);
        if (folder == null)
        {
            _logger.LogDebug("Folder {FolderId} not found locally, skipping folder share processing", share.FolderId);
            return;
        }

        // Check if we share this folder with the introducer
        if (!folder.Devices.Contains(introducerDeviceId))
        {
            _logger.LogDebug("Folder {FolderId} not shared with introducer {DeviceId}", share.FolderId, introducerDeviceId);
            return;
        }

        // Add introduced devices to this folder
        foreach (var deviceId in share.SharedWithDevices)
        {
            // Only add devices that were introduced (not existing devices)
            if (introducedDevices.Any(d => d.DeviceId == deviceId) && !folder.Devices.Contains(deviceId))
            {
                folder.AddDevice(deviceId);
                await _folderRepository.UpsertAsync(folder);

                result.FolderShareChanges.Add(new FolderShareChange
                {
                    FolderId = share.FolderId,
                    DeviceId = deviceId,
                    ChangeType = FolderShareChangeType.Added
                });
                result.ChangesMade = true;

                _logger.LogInformation("Added device {DeviceId} to folder {FolderId} via introduction",
                    deviceId, share.FolderId);
            }
        }
    }

    /// <inheritdoc />
    public async Task SetIntroducerAsync(string deviceId, bool isIntroducer, CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetAsync(deviceId);
        if (device == null)
        {
            throw new InvalidOperationException($"Device {deviceId} not found");
        }

        device.SetIntroducer(isIntroducer);
        await _deviceRepository.UpsertAsync(device);

        _logger.LogInformation("Device {DeviceId} ({DeviceName}) introducer status set to {IsIntroducer}",
            deviceId, device.DeviceName, isIntroducer);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SyncDevice>> GetIntroducedDevicesAsync(string introducerDeviceId, CancellationToken cancellationToken = default)
    {
        var allDevices = await _deviceRepository.GetAllAsync();
        return allDevices.Where(d => d.IntroducedBy == introducerDeviceId);
    }

    /// <inheritdoc />
    public async Task<bool> IsIntroducedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetAsync(deviceId);
        return device != null && !string.IsNullOrEmpty(device.IntroducedBy);
    }

    /// <inheritdoc />
    public async Task RemoveIntroducedDevicesAsync(string introducerDeviceId, CancellationToken cancellationToken = default)
    {
        // Check if the introducer has SkipIntroductionRemovals set
        var introducer = await _deviceRepository.GetAsync(introducerDeviceId);
        if (introducer != null && introducer.SkipIntroductionRemovals)
        {
            _logger.LogDebug("Introducer {DeviceId} has SkipIntroductionRemovals set, not removing introduced devices",
                introducerDeviceId);
            return;
        }

        var introducedDevices = await GetIntroducedDevicesAsync(introducerDeviceId, cancellationToken);
        var deviceList = introducedDevices.ToList();

        foreach (var device in deviceList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Remove device from all folders first
            var folders = await _folderRepository.GetFoldersSharedWithDeviceAsync(device.DeviceId);
            foreach (var folder in folders)
            {
                folder.RemoveDevice(device.DeviceId);
                await _folderRepository.UpsertAsync(folder);
            }

            // Delete the device
            await _deviceRepository.DeleteAsync(device.DeviceId);

            _logger.LogInformation("Removed introduced device {DeviceId} ({DeviceName}) because introducer {IntroducerDeviceId} was removed",
                device.DeviceId, device.DeviceName, introducerDeviceId);
        }

        // Update statistics
        lock (_statsLock)
        {
            _statistics.DevicesRemoved += deviceList.Count;
        }
    }

    /// <inheritdoc />
    public IntroducerServiceStatus GetStatus()
    {
        lock (_statsLock)
        {
            return new IntroducerServiceStatus
            {
                IsRunning = _isRunning,
                LastIntroductionTime = _lastIntroductionTime,
                Statistics = new IntroducerStatistics
                {
                    TotalIntroductionsProcessed = _statistics.TotalIntroductionsProcessed,
                    DevicesAdded = _statistics.DevicesAdded,
                    DevicesRemoved = _statistics.DevicesRemoved,
                    FolderSharesAdded = _statistics.FolderSharesAdded
                }
            };
        }
    }
}
