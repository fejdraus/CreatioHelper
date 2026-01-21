using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;

using SyncEventType = CreatioHelper.Domain.Entities.Events.SyncEventType;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Implementation of automatic folder acceptance.
/// Based on Syncthing's auto-accept logic from lib/model/model.go
///
/// Key behaviors:
/// - Check if device has AutoAcceptFolders enabled
/// - Check if folder is in device's ignored list
/// - Create folder in default path + folder label
/// - Add device to folder's device list
/// </summary>
public class AutoAcceptService : IAutoAcceptService
{
    private readonly ILogger<AutoAcceptService> _logger;
    private readonly IConfigurationManager _configManager;
    private readonly IPendingManager _pendingManager;
    private readonly IEventLogger _eventLogger;
    private readonly object _statsLock = new();

    private string _defaultFolderPath;
    private AutoAcceptStatistics _statistics = new();

    public AutoAcceptService(
        ILogger<AutoAcceptService> logger,
        IConfigurationManager configManager,
        IPendingManager pendingManager,
        IEventLogger eventLogger,
        string? defaultFolderPath = null)
    {
        _logger = logger;
        _configManager = configManager;
        _pendingManager = pendingManager;
        _eventLogger = eventLogger;
        _defaultFolderPath = defaultFolderPath ?? GetSystemDefaultPath();
    }

    /// <inheritdoc />
    public async Task<AutoAcceptResult> ProcessFolderOfferAsync(
        string deviceId,
        string folderId,
        string folderLabel,
        bool receiveEncrypted = false,
        CancellationToken cancellationToken = default)
    {
        var result = new AutoAcceptResult();

        try
        {
            lock (_statsLock)
            {
                _statistics.TotalOffersProcessed++;
            }

            // Check if folder already exists
            var existingFolder = await _configManager.GetFolderAsync(folderId);
            if (existingFolder != null)
            {
                // Folder exists - add device if not already shared
                if (!existingFolder.Devices.Contains(deviceId))
                {
                    existingFolder.AddDevice(deviceId);
                    await _configManager.UpsertFolderAsync(existingFolder);

                    _logger.LogInformation("Added device {DeviceId} to existing folder {FolderId}",
                        deviceId, folderId);
                }

                result.FolderAlreadyExists = true;
                result.Reason = "Folder already exists in configuration";
                return result;
            }

            // Get device
            var device = await _configManager.GetDeviceAsync(deviceId);
            if (device == null)
            {
                result.Reason = "Device not found";
                return result;
            }

            // Check if folder is in ignored list
            if (device.IgnoredFolders.Contains(folderId))
            {
                result.IsIgnored = true;
                result.Reason = "Folder is in device's ignored list";

                lock (_statsLock)
                {
                    _statistics.OffersIgnored++;
                }

                return result;
            }

            // Check if auto-accept is enabled for this device
            if (!device.AutoAcceptFolders)
            {
                // Add to pending instead
                await _pendingManager.AddPendingFolderAsync(new PendingFolder
                {
                    FolderId = folderId,
                    FolderLabel = folderLabel,
                    OfferedByDeviceId = deviceId,
                    OfferedByDeviceName = device.DeviceName,
                    ReceiveEncrypted = receiveEncrypted
                }, cancellationToken);

                result.AddedToPending = true;
                result.Reason = "Auto-accept not enabled for this device";

                lock (_statsLock)
                {
                    _statistics.OffersAddedToPending++;
                }

                return result;
            }

            // Auto-accept the folder
            var folderPath = GenerateFolderPath(folderLabel);
            var folderType = receiveEncrypted ? "receiveencrypted" : "sendreceive";

            var newFolder = new SyncFolder(folderId, folderLabel, folderPath, folderType);
            newFolder.AddDevice(deviceId);

            await _configManager.UpsertFolderAsync(newFolder);

            result.WasAutoAccepted = true;
            result.AcceptedFolder = newFolder;
            result.FolderPath = folderPath;

            lock (_statsLock)
            {
                _statistics.TotalFoldersAutoAccepted++;
            }

            _logger.LogInformation("Auto-accepted folder {FolderId} ({Label}) from device {DeviceId} to path {Path}",
                folderId, folderLabel, deviceId, folderPath);

            // Emit event
            _eventLogger.LogEvent(
                SyncEventType.FolderSummary, // Using FolderSummary as there's no specific FolderAdded event
                new
                {
                    Action = "auto-accepted",
                    FolderId = folderId,
                    FolderLabel = folderLabel,
                    Path = folderPath,
                    DeviceId = deviceId
                },
                $"Auto-accepted folder: {folderLabel}",
                deviceId: deviceId,
                folderId: folderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing folder offer {FolderId} from device {DeviceId}",
                folderId, deviceId);
            result.Reason = ex.Message;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetAutoAcceptAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default)
    {
        var device = await _configManager.GetDeviceAsync(deviceId);
        if (device == null)
        {
            throw new InvalidOperationException($"Device {deviceId} not found");
        }

        device.SetAutoAcceptFolders(enabled);
        await _configManager.UpsertDeviceAsync(device);

        _logger.LogInformation("Device {DeviceId} ({DeviceName}) auto-accept set to {Enabled}",
            deviceId, device.DeviceName, enabled);
    }

    /// <inheritdoc />
    public async Task<bool> IsAutoAcceptEnabledAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await _configManager.GetDeviceAsync(deviceId);
        return device?.AutoAcceptFolders ?? false;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SyncDevice>> GetAutoAcceptDevicesAsync(CancellationToken cancellationToken = default)
    {
        var allDevices = await _configManager.GetAllDevicesAsync();
        return allDevices.Where(d => d.AutoAcceptFolders);
    }

    /// <inheritdoc />
    public void SetDefaultFolderPath(string path)
    {
        _defaultFolderPath = path;
        _logger.LogDebug("Default folder path set to {Path}", path);
    }

    /// <inheritdoc />
    public AutoAcceptServiceStatus GetStatus()
    {
        return new AutoAcceptServiceStatus
        {
            IsActive = true,
            DefaultFolderPath = _defaultFolderPath,
            Statistics = new AutoAcceptStatistics
            {
                TotalFoldersAutoAccepted = _statistics.TotalFoldersAutoAccepted,
                TotalOffersProcessed = _statistics.TotalOffersProcessed,
                OffersIgnored = _statistics.OffersIgnored,
                OffersAddedToPending = _statistics.OffersAddedToPending
            }
        };
    }

    /// <summary>
    /// Generate folder path from default path and folder label.
    /// Sanitizes label for filesystem use.
    /// </summary>
    private string GenerateFolderPath(string folderLabel)
    {
        // Sanitize folder label for filesystem
        var sanitized = SanitizeFolderName(folderLabel);
        return Path.Combine(_defaultFolderPath, sanitized);
    }

    /// <summary>
    /// Sanitize a folder name for filesystem use.
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        // Replace invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

        // Trim and ensure not empty
        sanitized = sanitized.Trim();
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "folder";
        }

        return sanitized;
    }

    /// <summary>
    /// Get the system default sync folder path.
    /// </summary>
    private static string GetSystemDefaultPath()
    {
        // Use user's home directory / Sync
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homePath, "Sync");
    }
}
