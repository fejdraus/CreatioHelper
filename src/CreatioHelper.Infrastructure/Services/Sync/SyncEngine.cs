using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Application.DTOs;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;
using CreatioHelper.Infrastructure.Services.Sync.Relay;
using CreatioHelper.Infrastructure.Services.Sync.Scanning;
using CreatioHelper.Infrastructure.Services.Sync.Versioning;
using CreatioHelper.Infrastructure.Services.Network;
using CreatioHelper.Infrastructure.Services.Metrics;
using EventType = CreatioHelper.Domain.Entities.Events.SyncEventType;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Main synchronization engine implementation (based on Syncthing model)
/// Inspired by Syncthing's lib/model and lib/syncthing packages
/// </summary>
public class SyncEngine : ISyncEngine, IDisposable
{
    private readonly ILogger<SyncEngine> _logger;
    private readonly ISyncProtocol _protocol;
    private readonly IDeviceDiscovery _discovery;
    private readonly SyncthingGlobalDiscovery _globalDiscovery;
    private readonly ISyncDatabase _database;
    private readonly IConfigurationManager _configManager;
    private readonly IEventLogger _eventLogger;
    private readonly IStatisticsCollector _statisticsCollector;
    private readonly FileWatcher _fileWatcher;
    private readonly ConflictResolver _conflictResolver;
    private readonly FileComparator _fileComparator;
    private readonly FileDownloader _fileDownloader;
    private readonly FileUploader _fileUploader;
    private readonly BlockRequestHandler _blockRequestHandler;
    private readonly DeltaSyncEngine _deltaSyncEngine;
    private readonly BlockDuplicationDetector _blockDuplicationDetector;
    private readonly TransferOptimizer _transferOptimizer;
    private readonly RelayConnectionManager? _relayManager;
    private readonly ICombinedNatService? _natService;
    private readonly ICertificateManager _certificateManager;
    private readonly SyncFolderHandlerFactory _syncFolderHandlerFactory;
    private readonly IScanProgressService? _scanProgressService;
    private readonly IVersionerFactory? _versionerFactory;
    private readonly ConcurrentDictionary<string, IVersioner> _folderVersioners = new();
    private readonly ConcurrentDictionary<string, SyncDevice> _devices = new();
    private readonly ConcurrentDictionary<string, SyncFolder> _folders = new();
    private readonly ConcurrentDictionary<string, SyncStatus> _folderStatuses = new();
    private readonly ConcurrentDictionary<string, DeltaSyncPlan> _activeSyncPlans = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folderSyncSemaphores = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Timer _statusTimer;
    private readonly SyncStatistics _statistics = new();
    private readonly SyncConfiguration _configuration;
    private bool _isStarted = false;

    public event EventHandler<FolderSyncedEventArgs>? FolderSynced;
    public event EventHandler<ConflictDetectedEventArgs>? ConflictDetected;
    public event EventHandler<SyncErrorEventArgs>? SyncError;

    // ISyncEngine properties
    public string DeviceId => _configuration.DeviceId;

    public SyncEngine(
        ILogger<SyncEngine> logger,
        ISyncProtocol protocol,
        IDeviceDiscovery discovery,
        FileWatcher fileWatcher,
        ConflictResolver conflictResolver,
        FileComparator fileComparator,
        FileDownloader fileDownloader,
        FileUploader fileUploader,
        BlockRequestHandler blockRequestHandler,
        DeltaSyncEngine deltaSyncEngine,
        BlockDuplicationDetector blockDuplicationDetector,
        TransferOptimizer transferOptimizer,
        SyncConfiguration configuration,
        ISyncDatabase database,
        IConfigurationManager configManager,
        IEventLogger eventLogger,
        IStatisticsCollector statisticsCollector,
        ICertificateManager certificateManager,
        SyncFolderHandlerFactory syncFolderHandlerFactory,
        SyncthingGlobalDiscovery globalDiscovery,
        ICombinedNatService? natService = null,
        X509Certificate2? clientCertificate = null,
        IScanProgressService? scanProgressService = null,
        IVersionerFactory? versionerFactory = null)
    {
        _logger = logger;
        _protocol = protocol;
        _discovery = discovery;
        _globalDiscovery = globalDiscovery;
        _database = database;
        _configManager = configManager;
        _eventLogger = eventLogger;
        _statisticsCollector = statisticsCollector;
        _fileWatcher = fileWatcher;
        _conflictResolver = conflictResolver;
        _fileComparator = fileComparator;
        _fileDownloader = fileDownloader;
        _fileUploader = fileUploader;
        _blockRequestHandler = blockRequestHandler;
        _deltaSyncEngine = deltaSyncEngine;
        _blockDuplicationDetector = blockDuplicationDetector;
        _transferOptimizer = transferOptimizer;
        _configuration = configuration;
        _certificateManager = certificateManager;
        _syncFolderHandlerFactory = syncFolderHandlerFactory;
        _natService = natService;
        _scanProgressService = scanProgressService;
        _versionerFactory = versionerFactory;

        // Initialize relay manager if certificate is provided and relays are enabled
        if (clientCertificate != null && _configuration.RelaysEnabled)
        {
            var relayLogger = logger as ILogger<RelayConnectionManager> ?? 
                             Microsoft.Extensions.Logging.Abstractions.NullLogger<RelayConnectionManager>.Instance;
            _relayManager = new RelayConnectionManager(relayLogger, clientCertificate);
            _relayManager.RelayConnectionReceived += OnRelayConnectionReceived;
        }
        
        _statistics.StartTime = DateTime.UtcNow;
        
        // Set up periodic status updates
        _statusTimer = new Timer(UpdateStatistics, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        
        // Subscribe to events
        _protocol.DeviceConnected += OnDeviceConnected;
        _protocol.DeviceDisconnected += OnDeviceDisconnected;
        _protocol.IndexReceived += OnIndexReceived;
        _protocol.BlockRequestReceived += OnBlockRequestReceived;
        _discovery.DeviceDiscovered += OnDeviceDiscovered;
        _fileWatcher.FileChanged += OnFileChanged;
        _fileWatcher.FolderScanCompleted += OnFolderScanCompleted;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isStarted) return;

        _logger.LogInformation("Starting sync engine");

        // Initialize or load device certificate (similar to Syncthing's LoadOrGenerateCertificate)
        await InitializeDeviceCertificateAsync(cancellationToken);
        
        // Log system startup event
        _eventLogger.LogSystemEvent(EventType.Starting, "Starting sync engine", new { StartupTime = DateTime.UtcNow });

        try
        {
            // Initialize database
            await _database.InitializeAsync();
            _logger.LogInformation("Database initialized");

            // Initialize configuration manager (loads config.xml)
            await _configManager.InitializeAsync(cancellationToken);
            _logger.LogInformation("Configuration manager initialized");

            // Load existing configuration from config.xml
            await LoadConfigurationAsync();
            
            await _protocol.StartListeningAsync();
            await _discovery.StartAsync(cancellationToken);
            
            // Start Syncthing-compatible Global Discovery announcement loop
            _ = Task.Run(async () =>
            {
                try
                {
                    await _globalDiscovery.StartAnnouncementLoopAsync(() =>
                    {
                        // Return current listen addresses
                        return _configuration.ListenAddresses;
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Global Discovery announcement loop");
                }
            }, cancellationToken);
            
            // Start NAT traversal service if enabled
            if (_natService != null && _configuration.NatTraversal?.Enabled == true)
            {
                var natStarted = await _natService.StartAsync(cancellationToken);
                if (natStarted)
                {
                    // Create port mapping for sync protocol
                    var mapping = await _natService.CreateMappingAsync("tcp", _configuration.Port, 0, "CreatioHelper Sync");
                    if (mapping != null)
                    {
                        _logger.LogInformation("Created NAT port mapping: {Mapping}", mapping);
                    }
                }
            }
            
            // Start relay connections if enabled
            if (_relayManager != null)
            {
                await _relayManager.ConnectToRelaysAsync(_configuration.RelayServers);
            }
            
            _isStarted = true;
            _logger.LogInformation("Sync engine started successfully");
            
            // Log startup complete event
            _eventLogger.LogSystemEvent(EventType.StartupComplete, "Sync engine started successfully", new { 
                StartupTime = DateTime.UtcNow, 
                DeviceCount = _devices.Count, 
                FolderCount = _folders.Count 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start sync engine");
            _eventLogger.LogError(ex, "Sync engine startup failed");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isStarted) return;

        _logger.LogInformation("Stopping sync engine");
        
        // Log shutdown event
        _eventLogger.LogSystemEvent(EventType.Shutdown, "Stopping sync engine", new { ShutdownTime = DateTime.UtcNow });

        _cancellationTokenSource.Cancel();
        
        await _discovery.StopAsync();
        
        // Stop NAT service
        if (_natService != null)
        {
            await _natService.StopAsync();
        }
        
        // Stop relay connections
        if (_relayManager != null)
        {
            await _relayManager.DisconnectAllAsync();
        }
        
        // Disconnect all devices
        var disconnectTasks = _devices.Keys.Select(deviceId => _protocol.DisconnectAsync(deviceId));
        await Task.WhenAll(disconnectTasks);

        _isStarted = false;
        _logger.LogInformation("Sync engine stopped");
    }

    public async Task<SyncDevice> AddDeviceAsync(string deviceId, string name, string? certificateFingerprint = null, List<string>? addresses = null)
    {
        var device = new SyncDevice(deviceId, name);
        device.CertificateFingerprint = certificateFingerprint ?? string.Empty;
        
        if (addresses != null)
        {
            foreach (var address in addresses)
            {
                device.AddAddress(address);
            }
        }

        _devices[deviceId] = device;
        
        // Save device to config.xml
        try
        {
            await _configManager.UpsertDeviceAsync(device);
            _logger.LogInformation("Added and saved device {DeviceId} ({Name}) to config", deviceId, name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save device {DeviceId} to config", deviceId);
        }

        // Try to connect if we have addresses (resolve "dynamic" first like Syncthing)
        if (device.Addresses.Any())
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // Small delay to allow for setup
                await ConnectToDeviceAsync(device, _cancellationTokenSource.Token);
            });
        }

        return device;
    }

    public async Task<bool> RemoveDeviceAsync(string deviceId)
    {
        // Don't allow removing the local device
        if (deviceId == _configuration.DeviceId)
        {
            _logger.LogWarning("Cannot remove local device {DeviceId}", deviceId);
            return false;
        }

        if (!_devices.TryRemove(deviceId, out var device))
        {
            _logger.LogWarning("Device {DeviceId} not found for removal", deviceId);
            return false;
        }

        // Disconnect from the device if connected
        try
        {
            await _protocol.DisconnectAsync(deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting from device {DeviceId} during removal", deviceId);
        }

        // Remove device from all folders
        foreach (var folder in _folders.Values)
        {
            folder.RemoveDevice(deviceId);
        }

        // Remove from config.xml
        try
        {
            await _configManager.DeleteDeviceAsync(deviceId);
            _logger.LogInformation("Removed device {DeviceId} ({Name}) from config", deviceId, device.DeviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove device {DeviceId} from config", deviceId);
        }

        _logger.LogInformation("Removed device {DeviceId} ({Name})", deviceId, device.DeviceName);
        return true;
    }

    public async Task<SyncFolder> AddFolderAsync(string folderId, string label, string path, string type = "sendreceive")
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        var folder = new SyncFolder(folderId, label, path, type);
        _folders[folderId] = folder;
        
        // Initialize folder status
        _folderStatuses[folderId] = new SyncStatus
        {
            FolderId = folderId,
            State = SyncState.Idle
        };

        // Save folder to config.xml
        try
        {
            await _configManager.UpsertFolderAsync(folder);
            _logger.LogInformation("Added and saved folder {FolderId} ({Label}) to config", folderId, label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save folder {FolderId} to config", folderId);
        }

        // Register folder for block serving
        _blockRequestHandler.RegisterFolder(folder);

        // Start watching the folder
        _fileWatcher.WatchFolder(folder);

        // Perform initial scan
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000); // Allow folder watcher to initialize
            await ScanFolderAsync(folderId, deep: true);
        });

        _logger.LogInformation("Added folder {FolderId} ({Label}) at {Path}", folderId, label, path);

        return folder;
    }

    public async Task<SyncFolder> AddFolderAsync(FolderConfiguration config)
    {
        if (!Directory.Exists(config.Path))
        {
            Directory.CreateDirectory(config.Path);
        }

        // Create folder with all configuration settings
        var folder = new SyncFolder(
            config.Id,
            config.Label,
            config.Path,
            config.Type,
            config.RescanIntervalS,
            config.FsWatcherEnabled,
            (int)config.FsWatcherDelayS,
            config.IgnorePerms,
            config.AutoNormalize,
            config.MinDiskFree.ToString(),
            config.CopyOwnershipFromParent,
            config.ModTimeWindowS,
            config.MaxConflicts,
            config.DisableSparseFiles,
            config.DisableTempIndexes,
            config.Paused,
            config.WeakHashThresholdPct,
            config.MarkerName,
            config.CopyRangeMethod,
            config.CaseSensitiveFS,
            config.JunctionsAsDirs,
            config.SyncOwnership,
            config.SendOwnership,
            config.SyncXattrs,
            config.SendXattrs);

        // Set versioning if configured
        if (config.Versioning?.IsEnabled == true)
        {
            folder.SetVersioning(new VersioningConfiguration
            {
                Type = config.Versioning.Type,
                Params = config.Versioning.Params,
                CleanupIntervalS = config.Versioning.CleanupIntervalS,
                FSPath = config.Versioning.FsPath,
                FSType = config.Versioning.FsType
            });
        }

        // Set pull order
        if (!string.IsNullOrEmpty(config.Order))
        {
            folder.SetPullOrder(ParsePullOrder(config.Order));
        }

        // Set ignore delete
        folder.IgnoreDelete = config.IgnoreDelete;

        _folders[config.Id] = folder;

        // Initialize folder status
        _folderStatuses[config.Id] = new SyncStatus
        {
            FolderId = config.Id,
            State = config.Paused ? SyncState.Paused : SyncState.Idle
        };

        // Save folder to config.xml
        try
        {
            await _configManager.UpsertFolderAsync(folder);
            _logger.LogInformation("Added folder {FolderId} ({Label}) with full config to config.xml", config.Id, config.Label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save folder {FolderId} to config", config.Id);
        }

        // Register folder for block serving
        _blockRequestHandler.RegisterFolder(folder);

        // Add devices to folder
        foreach (var device in config.Devices)
        {
            folder.AddDevice(device.DeviceId);
        }

        // Start watching the folder if not paused
        if (!config.Paused)
        {
            _fileWatcher.WatchFolder(folder);

            // Perform initial scan
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                await ScanFolderAsync(config.Id, deep: true);
            });
        }

        _logger.LogInformation("Added folder {FolderId} ({Label}) at {Path} with full configuration", config.Id, config.Label, config.Path);

        return folder;
    }

    public async Task<SyncFolder> UpdateFolderAsync(FolderConfiguration config)
    {
        if (!_folders.TryGetValue(config.Id, out var existingFolder))
        {
            throw new ArgumentException($"Folder {config.Id} not found");
        }

        // Update folder settings
        existingFolder.RescanIntervalS = config.RescanIntervalS;
        existingFolder.FsWatcherEnabled = config.FsWatcherEnabled;
        existingFolder.FSWatcherDelayS = (int)config.FsWatcherDelayS;
        existingFolder.IgnorePerms = config.IgnorePerms;
        existingFolder.AutoNormalize = config.AutoNormalize;
        existingFolder.SetMinDiskFree(config.MinDiskFree.ToString());
        existingFolder.MaxConflicts = config.MaxConflicts;
        existingFolder.DisableSparseFiles = config.DisableSparseFiles;
        existingFolder.DisableTempIndexes = config.DisableTempIndexes;
        existingFolder.MarkerName = config.MarkerName;
        existingFolder.IgnoreDelete = config.IgnoreDelete;

        // Update versioning - invalidate cached versioner when config changes
        var versioningChanged = false;
        if (config.Versioning?.IsEnabled == true)
        {
            var oldConfig = existingFolder.Versioning;
            var newConfig = new VersioningConfiguration
            {
                Type = config.Versioning.Type,
                Params = config.Versioning.Params,
                CleanupIntervalS = config.Versioning.CleanupIntervalS,
                FSPath = config.Versioning.FsPath,
                FSType = config.Versioning.FsType
            };

            // Check if versioning config has changed
            versioningChanged = oldConfig == null || !oldConfig.IsEnabled ||
                               oldConfig.Type != newConfig.Type ||
                               oldConfig.FSPath != newConfig.FSPath;

            existingFolder.SetVersioning(newConfig);
        }
        else
        {
            versioningChanged = existingFolder.Versioning?.IsEnabled == true;
            existingFolder.SetVersioning(new VersioningConfiguration { Type = string.Empty });
        }

        // Invalidate cached versioner if versioning config changed
        if (versioningChanged && _folderVersioners.TryRemove(config.Id, out var oldVersioner))
        {
            _logger.LogDebug("Versioning configuration changed for folder {FolderId}, disposing old versioner", config.Id);
            try { oldVersioner?.Dispose(); } catch { /* ignore disposal errors */ }
        }

        // Update pull order
        if (!string.IsNullOrEmpty(config.Order))
        {
            existingFolder.SetPullOrder(ParsePullOrder(config.Order));
        }

        // Handle paused state change
        var wasPaused = existingFolder.IsPaused;
        if (config.Paused != wasPaused)
        {
            existingFolder.SetPaused(config.Paused);
            if (config.Paused)
            {
                _fileWatcher.StopWatchingFolder(config.Id);
                _folderStatuses[config.Id].State = SyncState.Paused;
            }
            else
            {
                _fileWatcher.WatchFolder(existingFolder);
                _folderStatuses[config.Id].State = SyncState.Idle;
            }
        }

        // Update devices
        var existingDevices = existingFolder.Devices.ToHashSet();
        var newDevices = config.Devices.Select(d => d.DeviceId).ToHashSet();

        foreach (var deviceId in existingDevices.Except(newDevices))
        {
            existingFolder.RemoveDevice(deviceId);
        }
        foreach (var deviceId in newDevices.Except(existingDevices))
        {
            existingFolder.AddDevice(deviceId);
        }

        // Save to config.xml
        try
        {
            await _configManager.UpsertFolderAsync(existingFolder);
            _logger.LogInformation("Updated folder {FolderId} configuration in config.xml", config.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update folder {FolderId} in config", config.Id);
        }

        // Restart file watcher with new settings if not paused and settings changed
        if (!config.Paused && !wasPaused)
        {
            _fileWatcher.StopWatchingFolder(config.Id);
            _fileWatcher.WatchFolder(existingFolder);
        }

        _logger.LogInformation("Updated folder {FolderId} ({Label}) configuration", config.Id, config.Label);

        return existingFolder;
    }

    private static Domain.Enums.SyncPullOrder ParsePullOrder(string order)
    {
        return order.ToLowerInvariant() switch
        {
            "alphabetic" => Domain.Enums.SyncPullOrder.Alphabetic,
            "smallestfirst" => Domain.Enums.SyncPullOrder.SmallestFirst,
            "largestfirst" => Domain.Enums.SyncPullOrder.LargestFirst,
            "oldestfirst" => Domain.Enums.SyncPullOrder.OldestFirst,
            "newestfirst" => Domain.Enums.SyncPullOrder.NewestFirst,
            _ => Domain.Enums.SyncPullOrder.Random
        };
    }

    public async Task ShareFolderWithDeviceAsync(string folderId, string deviceId)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            throw new ArgumentException($"Folder {folderId} not found");
        }

        if (!_devices.ContainsKey(deviceId))
        {
            throw new ArgumentException($"Device {deviceId} not found");
        }

        folder.AddDevice(deviceId);
        _logger.LogInformation("Shared folder {FolderId} with device {DeviceId}", folderId, deviceId);

        // Send cluster config to connected device
        if (await _protocol.IsConnectedAsync(deviceId))
        {
            await _protocol.SendClusterConfigAsync(deviceId, _folders.Values.ToList());
        }
    }

    public async Task UnshareFolderFromDeviceAsync(string folderId, string deviceId)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            throw new ArgumentException($"Folder {folderId} not found");
        }

        folder.RemoveDevice(deviceId);
        _logger.LogInformation("Unshared folder {FolderId} from device {DeviceId}", folderId, deviceId);

        // Send updated cluster config
        if (await _protocol.IsConnectedAsync(deviceId))
        {
            await _protocol.SendClusterConfigAsync(deviceId, _folders.Values.ToList());
        }
    }

    public Task PauseFolderAsync(string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            throw new ArgumentException($"Folder {folderId} not found");
        }

        folder.SetPaused(true);
        _fileWatcher.StopWatchingFolder(folderId);

        if (_folderStatuses.TryGetValue(folderId, out var status))
        {
            status.State = SyncState.Paused;
        }

        _logger.LogInformation("Paused folder {FolderId}", folderId);
        return Task.CompletedTask;
    }

    public async Task ResumeFolderAsync(string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            throw new ArgumentException($"Folder {folderId} not found");
        }

        folder.SetPaused(false);
        _fileWatcher.WatchFolder(folder);

        if (_folderStatuses.TryGetValue(folderId, out var status))
        {
            status.State = SyncState.Idle;
        }

        _logger.LogInformation("Resumed folder {FolderId}", folderId);

        // Trigger a scan
        await ScanFolderAsync(folderId);
    }

    public async Task PauseDeviceAsync(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device))
        {
            throw new ArgumentException($"Device {deviceId} not found");
        }

        device.SetPaused(true);
        await _protocol.DisconnectAsync(deviceId);

        _logger.LogInformation("Paused device {DeviceId}", deviceId);
    }

    public async Task ResumeDeviceAsync(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device))
        {
            throw new ArgumentException($"Device {deviceId} not found");
        }

        device.SetPaused(false);

        _logger.LogInformation("Resumed device {DeviceId}", deviceId);

        // Try to reconnect (resolve "dynamic" addresses first like Syncthing)
        await ConnectToDeviceAsync(device, _cancellationTokenSource.Token);
    }

    public async Task ScanFolderAsync(string folderId, bool deep = false)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            throw new ArgumentException($"Folder {folderId} not found");
        }

        if (folder.IsPaused)
        {
            _logger.LogDebug("Skipping scan of paused folder {FolderId}", folderId);
            FolderMetrics.SetState(folderId, FolderMetrics.State.Paused);
            return;
        }

        _logger.LogInformation("Scanning folder {FolderId} (deep: {Deep})", folderId, deep);

        // Set folder state to scanning and start timer
        FolderMetrics.SetState(folderId, FolderMetrics.State.Scanning);
        using var scanTimer = FolderMetrics.StartScan(folderId);

        if (_folderStatuses.TryGetValue(folderId, out var status))
        {
            status.State = SyncState.Scanning;
        }

        // Start scan progress tracking
        ScanProgressTracker? progressTracker = null;
        try
        {
            progressTracker = _scanProgressService?.StartScan(folderId);
            progressTracker?.SetPhase(ScanPhase.Enumerating);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start scan progress tracker for folder {FolderId}", folderId);
        }

        try
        {
            // Phase 1: Enumerate and scan files with progress reporting
            progressTracker?.SetPhase(ScanPhase.Enumerating);

            // Callback for when file estimates are ready (after quick enumeration)
            Action<long, long>? onEstimatesReady = progressTracker != null
                ? (fileCount, totalBytes) =>
                {
                    progressTracker.SetEstimates(fileCount, totalBytes);
                    progressTracker.SetPhase(ScanPhase.Scanning);
                }
                : null;

            // Callback for when each file is scanned
            Action<string, long>? onFileScanned = progressTracker != null
                ? (filePath, fileSize) => progressTracker.ReportFile(filePath, fileSize)
                : null;

            var files = await _fileWatcher.ScanFolderAsync(folder, onFileScanned, onEstimatesReady);
            folder.UpdateLastScan();

            // Phase 2: Store file metadata in database
            progressTracker?.SetPhase(ScanPhase.Updating);
            try
            {
                foreach (var file in files)
                {
                    var fileMetadata = new FileMetadata
                    {
                        FolderId = folderId,
                        FileName = file.RelativePath,
                        FileType = file.IsDirectory ? FileType.Directory : FileType.File,
                        Size = file.Size,
                        ModifiedTime = file.ModifiedTime,
                        DeviceId = _configuration.DeviceId,
                        Sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        VersionVector = file.Hash ?? string.Empty,
                        LocalFlags = file.IsSymlink ? (FileLocalFlags)4 : FileLocalFlags.None
                    };

                    await _database.FileMetadata.UpsertAsync(fileMetadata);
                }

                _logger.LogDebug("Stored {FileCount} file metadata entries for folder {FolderId}", files.Count, folderId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error storing file metadata to database for folder {FolderId}", folderId);
            }

            // Send index to connected devices
            var connectedDevices = folder.Devices
                .Where(d => _protocol.IsConnectedAsync(d).Result);

            foreach (var deviceId in connectedDevices)
            {
                await _protocol.SendIndexAsync(deviceId, folderId, files);
            }

            if (status != null)
            {
                status.State = SyncState.Idle;
                status.LastScan = DateTime.UtcNow;
                var fileCount = files.Count(f => !f.IsDirectory);
                var dirCount = files.Count(f => f.IsDirectory);
                status.LocalFiles = fileCount;
                status.LocalDirectories = dirCount;
                status.LocalBytes = files.Sum(f => f.Size);
                // Also set global state to match local (we're the source of truth)
                status.TotalFiles = fileCount;
                status.TotalDirectories = dirCount;
                status.TotalBytes = status.LocalBytes;
            }

            // Complete progress tracking
            progressTracker?.Complete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {FolderId}", folderId);

            progressTracker?.Fail(ex.Message);

            if (status != null)
            {
                status.State = SyncState.Error;
                status.Errors.Add($"Scan error: {ex.Message}");
            }

            SyncError?.Invoke(this, new SyncErrorEventArgs(folderId, $"Scan error: {ex.Message}", exception: ex));
        }
    }

    public Task<SyncStatus> GetSyncStatusAsync(string folderId)
    {
        return Task.FromResult(_folderStatuses.TryGetValue(folderId, out var status) 
            ? status 
            : new SyncStatus { FolderId = folderId, State = SyncState.Error });
    }

    public Task<List<SyncDevice>> GetDevicesAsync()
    {
        return Task.FromResult(_devices.Values.ToList());
    }

    public Task<List<SyncFolder>> GetFoldersAsync()
    {
        return Task.FromResult(_folders.Values.ToList());
    }

    public Task<SyncFolder?> GetFolderAsync(string folderId)
    {
        _folders.TryGetValue(folderId, out var folder);
        return Task.FromResult(folder);
    }

    public Task<SyncStatistics> GetStatisticsAsync()
    {
        _statistics.Uptime = DateTime.UtcNow - _statistics.StartTime;
        _statistics.ConnectedDevices = _devices.Values.Count(d => d.IsConnected);
        _statistics.TotalDevices = _devices.Count;
        _statistics.TotalFolders = _folders.Count;
        _statistics.SyncedFolders = _folderStatuses.Values.Count(s => s.State != SyncState.Error);

        return Task.FromResult(_statistics);
    }

    public Task<SyncConfiguration> GetConfigurationAsync()
    {
        return Task.FromResult(_configuration);
    }

    private async Task SendFolderIndexAsync(string deviceId, string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
            return;

        try
        {
            _logger.LogInformation("Sending Index for folder {FolderId} to device {DeviceId}", folderId, deviceId);

            // Receive-Only folders should not send their local files to other devices
            if (folder.SyncType == SyncFolderType.ReceiveOnly)
            {
                _logger.LogInformation("Folder {FolderId} is Receive-Only - sending empty index to device {DeviceId}", folderId, deviceId);
                await _protocol.SendIndexAsync(deviceId, folderId, new List<SyncFileInfo>());
                return;
            }

            // Get all files in the folder and scan them
            var files = await _fileWatcher.ScanFolderAsync(folder);

            await _protocol.SendIndexAsync(deviceId, folderId, files);
            
            _logger.LogInformation("Sent Index for folder {FolderId} with {FileCount} files to device {DeviceId}", 
                folderId, files.Count, deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending folder index for {FolderId} to {DeviceId}", folderId, deviceId);
        }
    }

    private async Task BroadcastUpdatedIndexAsync(string folderId)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
            return;

        try
        {
            _logger.LogInformation("Broadcasting updated Index for folder {FolderId} to all connected devices", folderId);

            // Receive-Only folders should not broadcast their local files
            if (folder.SyncType == SyncFolderType.ReceiveOnly)
            {
                _logger.LogInformation("Folder {FolderId} is Receive-Only - not broadcasting local files", folderId);
                return;
            }

            // Get current files after sync plan execution
            var files = await _fileWatcher.ScanFolderAsync(folder);
            
            // Send updated index to all connected devices that have access to this folder
            var broadcastTasks = new List<Task>();
            
            foreach (var deviceId in folder.Devices)
            {
                if (await _protocol.IsConnectedAsync(deviceId))
                {
                    _logger.LogDebug("Sending updated Index for folder {FolderId} to device {DeviceId}", folderId, deviceId);
                    broadcastTasks.Add(_protocol.SendIndexAsync(deviceId, folderId, files));
                }
            }
            
            await Task.WhenAll(broadcastTasks);
            
            _logger.LogInformation("Completed broadcasting updated Index for folder {FolderId} to {DeviceCount} devices", 
                folderId, broadcastTasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting updated index for folder {FolderId}", folderId);
        }
    }

    private async Task CollectFilesRecursiveAsync(DirectoryInfo directory, string rootPath, List<BepFileInfo> files)
    {
        try
        {
            // Process files in current directory
            foreach (var file in directory.GetFiles())
            {
                try
                {
                    var relativePath = Path.GetRelativePath(rootPath, file.FullName).Replace('\\', '/');
                    var fileInfo = await CreateFileInfoAsync(file, relativePath);
                    files.Add(fileInfo);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing file {FilePath}", file.FullName);
                }
            }

            // Process subdirectories
            foreach (var subDir in directory.GetDirectories())
            {
                await CollectFilesRecursiveAsync(subDir, rootPath, files);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error accessing directory {DirectoryPath}", directory.FullName);
        }
    }

    private async Task<BepFileInfo> CreateFileInfoAsync(System.IO.FileInfo file, string relativePath)
    {
        // Calculate file blocks (similar to Syncthing)
        var blockSize = CalculateBlockSize(file.Length);
        var blocks = new List<BepBlockInfo>();
        
        if (file.Length > 0)
        {
            blocks = await CalculateFileBlocksAsync(file.FullName, blockSize);
        }

        return new BepFileInfo
        {
            Name = relativePath,
            Size = file.Length,
            ModifiedS = new DateTimeOffset(file.LastWriteTime).ToUnixTimeSeconds(),
            Type = BepFileInfoType.File,
            Blocks = blocks,
            Sequence = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), // Simple sequence
            Version = new BepVector
            {
                Counters = new List<BepCounter>
                {
                    new BepCounter { Id = StringToShortId(_configuration.DeviceId), Value = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                }
            }
        };
    }

    private static ulong StringToShortId(string deviceIdHex)
    {
        try
        {
            var deviceId = Convert.FromHexString(deviceIdHex);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(deviceId.AsSpan(0, Math.Min(8, deviceId.Length)));
        }
        catch
        {
            return 0; // Fallback for invalid device IDs
        }
    }

    private static int CalculateBlockSize(long fileSize)
    {
        // Syncthing block size calculation
        const int desiredPerFileBlocks = 2000;
        var blockSizes = new[] { 128 * 1024, 256 * 1024, 512 * 1024, 1024 * 1024, 2048 * 1024, 4096 * 1024, 8192 * 1024, 16384 * 1024 };
        
        foreach (var size in blockSizes)
        {
            if (fileSize < desiredPerFileBlocks * size)
                return size;
        }
        
        return blockSizes[^1]; // Max block size
    }

    private async Task<List<BepBlockInfo>> CalculateFileBlocksAsync(string filePath, int blockSize)
    {
        var blocks = new List<BepBlockInfo>();
        
        using var file = File.OpenRead(filePath);
        var buffer = new byte[blockSize];
        long offset = 0;
        
        while (offset < file.Length)
        {
            var bytesToRead = (int)Math.Min(blockSize, file.Length - offset);
            var bytesRead = await file.ReadAsync(buffer.AsMemory(0, bytesToRead));
            
            if (bytesRead > 0)
            {
                var blockData = buffer.AsSpan(0, bytesRead).ToArray();
                var hash = System.Security.Cryptography.SHA256.HashData(blockData);
                
                blocks.Add(new BepBlockInfo
                {
                    Offset = offset,
                    Size = bytesRead,
                    Hash = hash
                });
                
                offset += bytesRead;
            }
        }
        
        return blocks;
    }

    private void OnDeviceConnected(object? sender, DeviceConnectedEventArgs e)
    {
        _logger.LogInformation("Device {DeviceId} connected", e.Device.DeviceId);

        // Record connection metrics
        ConnectionMetrics.RecordConnectionEstablished(e.Device.DeviceId, e.Device.ConnectionType ?? "TCP");
        ConnectionMetrics.SetTotalConnections(_devices.Count(d => d.Value.IsConnected));

        // Log device connection event
        _eventLogger.LogDeviceEvent(EventType.DeviceConnected, e.Device.DeviceId,
            $"Device connected: {e.Device.DeviceId}", new {
                DeviceId = e.Device.DeviceId,
                ConnectedAt = DateTime.UtcNow,
                ConnectionType = e.Device.ConnectionType ?? "TCP",
                Address = e.Device.LastAddress
            });

        // Record device connection in statistics
        _ = Task.Run(async () => {
            await _statisticsCollector.RecordDeviceConnectedAsync(
                e.Device.DeviceId,
                e.Device.ConnectionType ?? "TCP",
                e.Device.LastAddress ?? "unknown");
        });
        
        // Send cluster config and initial index to newly connected device
        _ = Task.Run(async () =>
        {
            await _protocol.SendClusterConfigAsync(e.Device.DeviceId, _folders.Values.ToList());
            
            // Send Index for each shared folder
            foreach (var folder in _folders.Values)
            {
                if (folder.Devices.Contains(e.Device.DeviceId))
                {
                    await SendFolderIndexAsync(e.Device.DeviceId, folder.Id);
                }
            }
        });
    }

    private void OnDeviceDisconnected(object? sender, DeviceDisconnectedEventArgs e)
    {
        _logger.LogInformation("Device {DeviceId} disconnected", e.DeviceId);

        // Record disconnection metrics
        ConnectionMetrics.RecordConnectionClosed(e.DeviceId, "disconnect");
        ConnectionMetrics.SetTotalConnections(_devices.Count(d => d.Value.IsConnected) - 1);

        // Log device disconnection event
        _eventLogger.LogDeviceEvent(EventType.DeviceDisconnected, e.DeviceId,
            $"Device disconnected: {e.DeviceId}", new {
                DeviceId = e.DeviceId,
                DisconnectedAt = DateTime.UtcNow
            });
        
        if (_devices.TryGetValue(e.DeviceId, out var device))
        {
            device.UpdateConnection(false);
            
            // Update last seen in configuration
            try
            {
                _configManager.UpdateDeviceLastSeen(e.DeviceId, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update last seen for device {DeviceId}", e.DeviceId);
            }
            
            // Record device disconnection in statistics
            var connectionDuration = device.LastConnected.HasValue ? DateTime.UtcNow - device.LastConnected.Value : TimeSpan.Zero;
            _ = Task.Run(async () => {
                await _statisticsCollector.RecordDeviceDisconnectedAsync(e.DeviceId, connectionDuration);
            });
        }
    }

    private async void OnIndexReceived(object? sender, IndexReceivedEventArgs e)
    {
        _logger.LogInformation("Received Index from device {DeviceId} for folder {FolderId} with {FileCount} files", 
            e.DeviceId, e.FolderId, e.Files.Count);

        // Log index received event
        _eventLogger.LogFolderEvent(EventType.RemoteIndexUpdated, e.FolderId, 
            $"Received index from {e.DeviceId}: {e.Files.Count} files", new { 
                DeviceId = e.DeviceId,
                FolderId = e.FolderId,
                FileCount = e.Files.Count,
                ReceivedAt = DateTime.UtcNow 
            });

        try
        {
            await ProcessReceivedIndexAsync(e.DeviceId, e.FolderId, e.Files);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing received index from {DeviceId} for folder {FolderId}", 
                e.DeviceId, e.FolderId);
        }
    }

    private async Task ProcessReceivedIndexAsync(string deviceId, string folderId, List<SyncFileInfo> remoteFiles)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            _logger.LogWarning("Received index for unknown folder {FolderId} from device {DeviceId}", folderId, deviceId);
            return;
        }

        // Send-Only folders should not process incoming indexes (should not download files)
        if (folder.SyncType == SyncFolderType.SendOnly)
        {
            _logger.LogInformation("Folder {FolderId} is Send-Only - ignoring incoming index from device {DeviceId}", folderId, deviceId);
            return;
        }

        // Check if device is allowed to share this folder
        if (!folder.Devices.Contains(deviceId))
        {
            _logger.LogWarning("Device {DeviceId} is not authorized for folder {FolderId}", deviceId, folderId);
            return;
        }

        _logger.LogInformation("Processing {FileCount} files from remote index for folder {FolderId}", 
            remoteFiles.Count, folderId);

        // Use semaphore to prevent concurrent processing of the same folder
        var semaphore = _folderSyncSemaphores.GetOrAdd(folderId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(_cancellationTokenSource.Token);

        try
        {
            if (_folderStatuses.TryGetValue(folderId, out var status))
            {
                status.State = SyncState.Syncing;
                status.GlobalFiles = remoteFiles.Count;
                status.GlobalBytes = remoteFiles.Sum(f => f.Size);
            }

            // Get current local files
            var localFiles = await _fileWatcher.ScanFolderAsync(folder);
            
            // Create sync plan by comparing files
            var syncPlan = _fileComparator.CreateSyncPlan(folderId, deviceId, localFiles, remoteFiles);
            
            _logger.LogInformation("Sync plan for folder {FolderId}: {Downloads} downloads ({DownloadBytes} bytes), {Uploads} uploads ({UploadBytes} bytes), {Deletes} deletes, {Conflicts} conflicts",
                folderId, syncPlan.TotalFilesToDownload, syncPlan.TotalBytesToDownload, 
                syncPlan.TotalFilesToUpload, syncPlan.TotalBytesToUpload, 
                syncPlan.FilesToDelete.Count, syncPlan.Conflicts.Count);

            // Create optimized transfer plans using block-level deduplication
            await CreateOptimizedTransferPlansAsync(deviceId, folderId, localFiles, syncPlan.FilesToDownload);

            // Execute sync plan
            if (syncPlan.HasWork)
            {
                await ExecuteSyncPlanAsync(folder, syncPlan);
                
                // After executing sync plan, send updated index to all connected devices
                // This ensures other devices know about any new/changed files
                await BroadcastUpdatedIndexAsync(folderId);
            }

            if (status != null)
            {
                status.State = SyncState.Idle;
                status.LastSync = DateTime.UtcNow;
            }

            _logger.LogInformation("Completed processing index for folder {FolderId}", folderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing remote index for folder {FolderId}", folderId);
            
            if (_folderStatuses.TryGetValue(folderId, out var status))
            {
                status.State = SyncState.Error;
                status.Errors.Add($"Index processing error: {ex.Message}");
            }

            SyncError?.Invoke(this, new SyncErrorEventArgs(folderId, $"Index processing error: {ex.Message}", deviceId, ex));
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ExecuteSyncPlanAsync(SyncFolder folder, SyncPlan syncPlan)
    {
        _logger.LogInformation("Executing sync plan for folder {FolderId} (Type: {FolderType})", folder.Id, folder.SyncType);

        // Set folder state to syncing
        FolderMetrics.SetState(folder.Id, FolderMetrics.State.Syncing);

        // Get the appropriate folder handler based on folder type
        var folderHandler = _syncFolderHandlerFactory.CreateHandler(folder);
        _logger.LogDebug("Using handler {HandlerType} for folder {FolderId}", folderHandler.GetType().Name, folder.Id);

        var syncSummary = new SyncSummary();
        var syncStartTime = DateTime.UtcNow;

        try
        {
            // Check if this folder type can receive changes (downloads)
            if (folderHandler.CanReceiveChanges && syncPlan.FilesToDownload.Any())
            {
                _logger.LogInformation("Processing {Count} downloads for {FolderType} folder {FolderId}", 
                    syncPlan.FilesToDownload.Count, folder.SyncType, folder.Id);
                    
                // Process downloads first (get new/updated files)
                foreach (var downloadAction in syncPlan.FilesToDownload)
                {
                    try
                    {
                        _logger.LogInformation("Downloading file: {FileName} ({Reason})", 
                            downloadAction.FileName, downloadAction.Reason);

                        // Find the remote file info for this download
                        // We need to get it from the sync plan or store it properly
                        // For now, implement a placeholder that will be completed when we have the complete file info
                        await DownloadFileFromPlanAsync(folder, syncPlan.DeviceId, downloadAction);

                        syncSummary.FilesTransferred++;
                        syncSummary.BytesTransferred += downloadAction.FileSize;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error downloading file {FileName}", downloadAction.FileName);
                        syncSummary.Errors.Add(ex.Message);
                    }
                }
            }
            else if (!folderHandler.CanReceiveChanges && syncPlan.FilesToDownload.Any())
            {
                _logger.LogInformation("Skipping {Count} downloads for {FolderType} folder {FolderId} - folder type cannot receive changes", 
                    syncPlan.FilesToDownload.Count, folder.SyncType, folder.Id);
            }

            // Check if this folder type can send changes (uploads) - логика не очень правильная, но сейчас uploads не реализованы
            if (folderHandler.CanSendChanges && syncPlan.FilesToUpload.Any())
            {
                _logger.LogInformation("Processing {Count} uploads for {FolderType} folder {FolderId}", 
                    syncPlan.FilesToUpload.Count, folder.SyncType, folder.Id);
                    
                // Process uploads (send new/updated files)
                foreach (var uploadAction in syncPlan.FilesToUpload)
                {
                    try
                    {
                        _logger.LogInformation("Uploading file: {FileName} ({Reason})",
                            uploadAction.FileName, uploadAction.Reason);

                        await UploadFileFromPlanAsync(folder, syncPlan.DeviceId, uploadAction);

                        syncSummary.FilesTransferred++;
                        syncSummary.BytesTransferred += uploadAction.FileSize;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error uploading file {FileName}", uploadAction.FileName);
                        syncSummary.Errors.Add(ex.Message);
                    }
                }
            }
            else if (!folderHandler.CanSendChanges && syncPlan.FilesToUpload.Any())
            {
                _logger.LogInformation("Skipping {Count} uploads for {FolderType} folder {FolderId} - folder type cannot send changes", 
                    syncPlan.FilesToUpload.Count, folder.SyncType, folder.Id);
            }

            // Process deletions
            foreach (var deleteAction in syncPlan.FilesToDelete)
            {
                try
                {
                    _logger.LogInformation("Deleting file: {FileName} ({Reason})", 
                        deleteAction.FileName, deleteAction.Reason);

                    var localFilePath = Path.Combine(folder.Path, deleteAction.FileName);
                    if (File.Exists(localFilePath))
                    {
                        File.Delete(localFilePath);
                        _logger.LogDebug("Deleted local file: {FilePath}", localFilePath);
                    }

                    syncSummary.FilesTransferred++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting file {FileName}", deleteAction.FileName);
                    syncSummary.Errors.Add(ex.Message);
                }
            }

            // Handle conflicts using folder-type-specific handler
            if (syncPlan.Conflicts.Any())
            {
                try
                {
                    // Get the appropriate handler for this folder type
                    var handler = _syncFolderHandlerFactory.CreateHandler(folder);

                    foreach (var conflict in syncPlan.Conflicts)
                    {
                        try
                        {
                            _logger.LogWarning("Handling conflict: {FileName} ({ConflictType}) using {HandlerType}",
                                conflict.FileName, conflict.ConflictType, handler.GetType().Name);

                            // Resolve conflict through the handler
                            await handler.ResolveConflictsAsync(folder, new[] { conflict }, _cancellationTokenSource.Token);

                            syncSummary.Conflicts++;

                            ConflictDetected?.Invoke(this, new ConflictDetectedEventArgs(folder.Id, conflict.FileName, new List<ConflictVersion>
                            {
                                new() { DeviceId = "local", ModifiedTime = conflict.LocalFile.ModifiedTime, Size = conflict.LocalFile.Size, Hash = conflict.LocalFile.Hash },
                                new() { DeviceId = syncPlan.DeviceId, ModifiedTime = conflict.RemoteFile.ModifiedTime, Size = conflict.RemoteFile.Size, Hash = conflict.RemoteFile.Hash }
                            }));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error handling conflict for file {FileName}", conflict.FileName);
                            syncSummary.Errors.Add(ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating handler for folder {FolderId}", folder.Id);
                    syncSummary.Errors.Add($"Failed to create conflict handler: {ex.Message}");
                }
            }

            // Update statistics
            _statistics.TotalFilesReceived += syncSummary.FilesTransferred;
            _statistics.TotalBytesIn += syncSummary.BytesTransferred;

            // Record folder metrics
            var syncDuration = (DateTime.UtcNow - syncStartTime).TotalSeconds;
            FolderMetrics.RecordPull(folder.Id, syncDuration, syncSummary.BytesTransferred);

            // Record conflict metrics
            if (syncSummary.Conflicts > 0)
            {
                for (int i = 0; i < syncSummary.Conflicts; i++)
                    FolderMetrics.RecordConflict(folder.Id);
            }

            // Record error metrics
            foreach (var error in syncSummary.Errors)
            {
                FolderMetrics.RecordError(folder.Id, "sync_error");
            }

            // Set folder state back to idle
            FolderMetrics.SetState(folder.Id, FolderMetrics.State.Idle);

            FolderSynced?.Invoke(this, new FolderSyncedEventArgs(folder.Id, syncSummary));

            _logger.LogInformation("Sync plan executed for folder {FolderId}: {FilesTransferred} files, {BytesTransferred} bytes, {Conflicts} conflicts, {Errors} errors",
                folder.Id, syncSummary.FilesTransferred, syncSummary.BytesTransferred, syncSummary.Conflicts, syncSummary.Errors.Count);
        }
        catch (Exception ex)
        {
            // Record error state
            FolderMetrics.SetState(folder.Id, FolderMetrics.State.Error);
            FolderMetrics.RecordError(folder.Id, ex.GetType().Name);

            _logger.LogError(ex, "Error executing sync plan for folder {FolderId}", folder.Id);
            throw;
        }
    }

    /// <summary>
    /// Gets or creates a versioner for the specified folder.
    /// Versioners are cached to avoid recreating them for each file operation.
    /// Based on Syncthing's versioning model where versioning only triggers on REMOTE changes.
    /// </summary>
    private IVersioner? GetOrCreateVersioner(SyncFolder folder)
    {
        if (_versionerFactory == null || folder.Versioning == null || !folder.Versioning.IsEnabled)
        {
            return null;
        }

        return _folderVersioners.GetOrAdd(folder.Id, _ =>
        {
            _logger.LogDebug("Creating versioner for folder {FolderId}: {VersionerType}",
                folder.Id, folder.Versioning.Type);
            return _versionerFactory.CreateVersioner(folder.Path, folder.Versioning);
        });
    }

    /// <summary>
    /// Archives a file before it's overwritten by a remote change.
    /// This implements Syncthing's versioning behavior where versioning only triggers on REMOTE changes.
    /// </summary>
    private async Task ArchiveFileBeforeOverwriteAsync(SyncFolder folder, string relativePath, CancellationToken cancellationToken)
    {
        var versioner = GetOrCreateVersioner(folder);
        if (versioner == null)
        {
            return; // Versioning not enabled for this folder
        }

        var fullPath = Path.Combine(folder.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return; // No existing file to archive
        }

        try
        {
            _logger.LogDebug("Archiving file before remote overwrite: {FilePath} (versioner: {VersionerType})",
                relativePath, versioner.VersionerType);
            await versioner.ArchiveAsync(relativePath, cancellationToken);
            _logger.LogInformation("Archived file {FilePath} before remote update", relativePath);

            // Log versioning event
            _eventLogger.LogFileEvent(EventType.FileVersioned, folder.Id, relativePath,
                $"File archived before remote update: {relativePath}",
                new
                {
                    FolderId = folder.Id,
                    FilePath = relativePath,
                    VersionerType = versioner.VersionerType,
                    VersionsPath = versioner.VersionsPath,
                    ArchivedAt = DateTime.UtcNow
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to archive file {FilePath} before remote overwrite", relativePath);
            // Don't fail the sync operation if versioning fails - log and continue
        }
    }

    private async Task DownloadFileFromPlanAsync(SyncFolder folder, string deviceId, FileAction downloadAction)
    {
        if (downloadAction.FileInfo == null)
        {
            _logger.LogWarning("Cannot download {FileName}: missing file info", downloadAction.FileName);
            return;
        }

        var localFilePath = Path.Combine(folder.Path, downloadAction.FileName.Replace('/', Path.DirectorySeparatorChar));

        // Archive existing file before overwriting (versioning only on REMOTE changes per Syncthing spec)
        await ArchiveFileBeforeOverwriteAsync(folder, downloadAction.FileName, _cancellationTokenSource.Token);

        _logger.LogInformation("Downloading {FileName} to {LocalPath}", downloadAction.FileName, localFilePath);

        var result = await _fileDownloader.DownloadFileAsync(
            deviceId,
            folder.Id,
            downloadAction.FileInfo,
            localFilePath,
            _cancellationTokenSource.Token);

        if (result.Success)
        {
            _logger.LogInformation("Successfully downloaded {FileName}: {BytesTransferred} bytes in {Duration}ms",
                result.FileName, result.BytesTransferred, result.Duration.TotalMilliseconds);
        }
        else
        {
            _logger.LogError("Failed to download {FileName}: {Error}", result.FileName, result.Error);
            throw new InvalidOperationException($"Download failed: {result.Error}");
        }
    }

    private async Task UploadFileFromPlanAsync(SyncFolder folder, string deviceId, FileAction uploadAction)
    {
        var localFilePath = Path.Combine(folder.Path, uploadAction.FileName.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(localFilePath))
        {
            _logger.LogWarning("Cannot upload {FileName}: local file not found at {LocalPath}",
                uploadAction.FileName, localFilePath);
            return;
        }

        _logger.LogInformation("Uploading {FileName} from {LocalPath} to device {DeviceId}",
            uploadAction.FileName, localFilePath, deviceId);

        // Build SyncFileInfo if not provided
        SyncFileInfo fileInfo;
        if (uploadAction.FileInfo != null)
        {
            fileInfo = uploadAction.FileInfo;
        }
        else
        {
            // Create file info from local file
            var localFileInfo = new FileInfo(localFilePath);
            fileInfo = new SyncFileInfo(
                folder.Id,
                uploadAction.FileName,
                uploadAction.FileName,
                localFileInfo.Length,
                localFileInfo.LastWriteTimeUtc);

            // Calculate blocks
            var blocks = await _fileUploader.CalculateFileBlocksAsync(localFilePath, _cancellationTokenSource.Token);
            fileInfo.SetBlocks(blocks);
        }

        // Use delta upload if remote file info is available
        if (uploadAction.RemoteFileInfo != null)
        {
            var deltaResult = await _fileUploader.DeltaUploadAsync(
                deviceId,
                folder.Id,
                localFilePath,
                fileInfo,
                uploadAction.RemoteFileInfo,
                _cancellationTokenSource.Token);

            if (deltaResult.Success)
            {
                if (deltaResult.IsDeltaUpload)
                {
                    _logger.LogInformation(
                        "Delta upload of {FileName}: {ChangedBlocks}/{TotalBlocks} blocks changed, " +
                        "{ChangedBytes}/{TotalBytes} bytes transferred ({TransferPercent:F1}%), saved {BytesSaved} bytes in {Duration}ms",
                        deltaResult.FileName,
                        deltaResult.ChangedBlocks,
                        deltaResult.TotalBlocks,
                        deltaResult.ChangedBytes,
                        deltaResult.TotalBytes,
                        deltaResult.TransferPercentage,
                        deltaResult.BytesSaved,
                        deltaResult.Duration.TotalMilliseconds);
                }
                else
                {
                    _logger.LogInformation("Full upload of {FileName}: {TotalBytes} bytes in {Duration}ms (no remote info available)",
                        deltaResult.FileName, deltaResult.TotalBytes, deltaResult.Duration.TotalMilliseconds);
                }
            }
            else
            {
                _logger.LogError("Failed delta upload of {FileName}: {Error}", deltaResult.FileName, deltaResult.Error);
                throw new InvalidOperationException($"Delta upload failed: {deltaResult.Error}");
            }
        }
        else
        {
            // Fall back to full upload
            var result = await _fileUploader.UploadFileAsync(
                deviceId,
                folder.Id,
                localFilePath,
                fileInfo,
                _cancellationTokenSource.Token);

            if (result.Success)
            {
                _logger.LogInformation("Full upload of {FileName}: {BytesTransferred} bytes in {Duration}ms",
                    result.FileName, result.BytesTransferred, result.Duration.TotalMilliseconds);
            }
            else
            {
                _logger.LogError("Failed to upload {FileName}: {Error}", result.FileName, result.Error);
                throw new InvalidOperationException($"Upload failed: {result.Error}");
            }
        }
    }

    private async void OnBlockRequestReceived(object? sender, BlockRequestReceivedEventArgs e)
    {
        try
        {
            _logger.LogTrace("Received block request from device {DeviceId}", e.DeviceId);

            // Cast the request object to BepRequest (avoiding circular dependency in interface)
            if (e.Request is not BepRequest request)
            {
                _logger.LogWarning("Invalid block request object type from device {DeviceId}", e.DeviceId);
                return;
            }

            await _blockRequestHandler.HandleBlockRequestAsync(e.DeviceId, request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling block request from device {DeviceId}", e.DeviceId);
        }
    }

    /// <summary>
    /// Resolves device addresses, replacing "dynamic" with actual addresses from discovery.
    /// Based on Syncthing's resolveDeviceAddrs in lib/connections/service.go
    /// </summary>
    private async Task<List<string>> ResolveDeviceAddressesAsync(SyncDevice device, CancellationToken cancellationToken)
    {
        var resolvedAddresses = new List<string>();

        foreach (var addr in device.Addresses)
        {
            if (addr == "dynamic")
            {
                // Lookup addresses via discovery (like Syncthing's discoverer.Lookup)
                try
                {
                    var discovered = await _discovery.DiscoverAsync(device.DeviceId, cancellationToken);
                    foreach (var d in discovered)
                    {
                        resolvedAddresses.AddRange(d.Addresses);
                    }

                    // Also try global discovery
                    if (_globalDiscovery != null)
                    {
                        var globalAddresses = await _globalDiscovery.LookupAsync(device.DeviceId, cancellationToken);
                        resolvedAddresses.AddRange(globalAddresses);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Discovery lookup failed for device {DeviceId}", device.DeviceId);
                }
            }
            else
            {
                // Static address - use directly
                resolvedAddresses.Add(addr);
            }
        }

        // Remove duplicates and empty entries
        return resolvedAddresses
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Connects to a device after resolving "dynamic" addresses.
    /// Based on Syncthing's connection flow in lib/connections/service.go
    /// </summary>
    private async Task<bool> ConnectToDeviceAsync(SyncDevice device, CancellationToken cancellationToken)
    {
        // Resolve "dynamic" addresses first (like Syncthing's resolveDeviceAddrs)
        var resolvedAddresses = await ResolveDeviceAddressesAsync(device, cancellationToken);

        if (resolvedAddresses.Count == 0)
        {
            _logger.LogDebug("No addresses resolved for device {DeviceId}", device.DeviceId);
            return false;
        }

        _logger.LogDebug("Resolved {Count} addresses for device {DeviceId}: {Addresses}",
            resolvedAddresses.Count, device.DeviceId, string.Join(", ", resolvedAddresses));

        // Update device with resolved addresses (add to existing, don't replace "dynamic")
        foreach (var addr in resolvedAddresses)
        {
            device.AddAddress(addr);
        }

        // Now connect using resolved addresses
        return await _protocol.ConnectAsync(device, cancellationToken);
    }

    private void OnDeviceDiscovered(object? sender, DeviceDiscoveredEventArgs e)
    {
        _logger.LogDebug("Discovered device {DeviceId} at {Addresses}",
            e.Device.DeviceId, string.Join(", ", e.Device.Addresses));

        if (_devices.TryGetValue(e.Device.DeviceId, out var device))
        {
            foreach (var address in e.Device.Addresses)
            {
                device.AddAddress(address);
            }

            // Try to connect if not already connected
            if (!device.IsConnected && !device.IsPaused)
            {
                _ = Task.Run(async () => 
                {
                    var connected = await _protocol.ConnectAsync(device, _cancellationTokenSource.Token);
                    
                    // If direct connection failed, try relay
                    if (!connected && _relayManager != null)
                    {
                        _logger.LogDebug("Direct connection failed for {DeviceId}, trying relay", device.DeviceId);
                        await TryConnectThroughRelayAsync(device.DeviceId, TimeSpan.FromSeconds(30));
                    }
                });
            }
        }
    }



    private async Task DownloadFileAsync(string deviceId, SyncFolder folder, SyncFileInfo remoteFile)
    {
        try
        {
            var localFilePath = Path.Combine(folder.Path, remoteFile.RelativePath);
            var localDir = Path.GetDirectoryName(localFilePath);

            if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
            {
                Directory.CreateDirectory(localDir);
            }

            // Archive existing file before overwriting (versioning only on REMOTE changes per Syncthing spec)
            await ArchiveFileBeforeOverwriteAsync(folder, remoteFile.RelativePath, _cancellationTokenSource.Token);

            using var fileStream = File.Create(localFilePath);

            foreach (var block in remoteFile.Blocks)
            {
                var blockData = await _protocol.RequestBlockAsync(deviceId, folder.Id, remoteFile.Name, block.Offset, block.Size, block.Hash);
                await fileStream.WriteAsync(blockData);
            }

            // Set file modification time
            File.SetLastWriteTimeUtc(localFilePath, remoteFile.ModifiedTime);

            _logger.LogDebug("Downloaded file {FileName} from device {DeviceId}", remoteFile.Name, deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading file {FileName} from device {DeviceId}", remoteFile.Name, deviceId);
            throw;
        }
    }

    private void OnFileChanged(object? sender, FileChangedEventArgs e)
    {
        _logger.LogDebug("File changed in folder {FolderId}: {FilePath} ({ChangeType})", 
            e.FolderId, e.FilePath, e.ChangeType);

        // Log file change event
        _eventLogger.LogFileEvent(EventType.LocalChangeDetected, e.FolderId, e.FilePath, 
            $"File {e.ChangeType}: {e.FilePath}", new { 
                FolderId = e.FolderId,
                FilePath = e.FilePath,
                ChangeType = e.ChangeType.ToString(),
                DetectedAt = DateTime.UtcNow 
            });

        // Record file processing in statistics
        _ = Task.Run(async () => {
            await _statisticsCollector.RecordFileProcessedAsync(
                e.FolderId, 
                e.FilePath, 
                e.ChangeType == FileChangeType.Deleted, 
                e.FileSize,
                e.ChangeType.ToString());
        });

        // Trigger a partial scan for this folder
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000); // Debounce multiple rapid changes
            await ScanFolderAsync(e.FolderId);
        });
    }

    private void OnFolderScanCompleted(object? sender, FolderScanCompletedEventArgs e)
    {
        _logger.LogDebug("Completed scan of folder {FolderId}: {FileCount} files in {Duration}ms",
            e.FolderId, e.Files.Count, e.Duration.TotalMilliseconds);

        // Record folder metrics
        FolderMetrics.SetState(e.FolderId, FolderMetrics.State.Idle);
        FolderMetrics.SetFileCount(e.FolderId, "local", e.Files.Count);
        var totalSize = e.Files.Sum(f => f.Size);
        FolderMetrics.SetTotalBytes(e.FolderId, "local", totalSize);
        FolderMetrics.RecordLastScan(e.FolderId);

        // Log folder scan completed event
        _eventLogger.LogFolderEvent(EventType.FolderScanComplete, e.FolderId,
            $"Folder scan completed: {e.Files.Count} files in {e.Duration.TotalMilliseconds:F1}ms", new {
                FolderId = e.FolderId,
                FileCount = e.Files.Count,
                TotalSize = totalSize,
                Duration = e.Duration.TotalMilliseconds,
                CompletedAt = DateTime.UtcNow
            });

        // Record folder scan statistics
        _ = Task.Run(async () => {
            await _statisticsCollector.RecordFolderScanCompletedAsync(e.FolderId, e.Files.Count, totalSize);
        });

        if (_folderStatuses.TryGetValue(e.FolderId, out var status))
        {
            var fileCount = e.Files.Count(f => !f.IsDirectory);
            var dirCount = e.Files.Count(f => f.IsDirectory);
            status.LocalFiles = fileCount;
            status.LocalDirectories = dirCount;
            status.LocalBytes = totalSize;
            status.LastScan = DateTime.UtcNow;
            // Also set global state to match local (we're the source of truth)
            status.TotalFiles = fileCount;
            status.TotalDirectories = dirCount;
            status.TotalBytes = totalSize;
        }
    }

    private void UpdateStatistics(object? state)
    {
        // Update runtime statistics
        _statistics.Uptime = DateTime.UtcNow - _statistics.StartTime;
        _statistics.ConnectedDevices = _devices.Values.Count(d => d.IsConnected);
        
        // Update system statistics in StatisticsCollector
        _ = Task.Run(async () => {
            var connectedDevices = _devices.Values.Count(d => d.IsConnected);
            var totalDevices = _devices.Count;
            var activeFolders = _folders.Count;
            var totalDataSize = _folderStatuses.Values.Sum(s => s.LocalBytes);
            
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var memoryUsage = process.WorkingSet64;
            var cpuUsage = 0.0; // Syncthing не отслеживает CPU usage
            var threadCount = System.Threading.ThreadPool.ThreadCount;
            var openFiles = 0; // Syncthing не отслеживает количество открытых файлов
            
            await _statisticsCollector.UpdateSystemStatisticsAsync(
                connectedDevices, totalDevices, activeFolders, activeFolders, 
                totalDataSize, memoryUsage, cpuUsage, threadCount, openFiles);
                
            // Syncthing отслеживает только базовые метрики через Prometheus counters
            // Детальные метрики производительности не являются core функционалом
            var fileScanRate = 0.0;
            var indexingRate = 0.0;
            var networkLatency = 0.0; // Syncthing использует только throughput
            var diskThroughput = 0.0;
            var buffersUsed = 0; // .NET управляет буферами внутренне
            var maxBuffers = 1000;
            var activeConnections = connectedDevices;
            var syncQueueLength = _activeSyncPlans.Count;
            
            await _statisticsCollector.RecordPerformanceMetricsAsync(
                fileScanRate, indexingRate, networkLatency, diskThroughput,
                buffersUsed, maxBuffers, activeConnections, syncQueueLength);
        });
    }

    /// <summary>
    /// Creates optimized transfer plans using Syncthing-style block-level deduplication
    /// Uses position-based block comparison and SHA-256 hash matching for maximum efficiency
    /// </summary>
    private async Task CreateOptimizedTransferPlansAsync(string deviceId, string folderId, List<SyncFileInfo> localFiles, List<FileAction> filesToDownload)
    {
        _logger.LogInformation("Creating Syncthing-style optimized transfer plans for {FileCount} files from device {DeviceId}", 
            filesToDownload.Count, deviceId);

        try
        {
            var totalOriginalSize = 0L;
            var totalOptimizedSize = 0L;
            var totalBlocksDeduped = 0L;
            
            foreach (var downloadAction in filesToDownload)
            {
                totalOriginalSize += downloadAction.FileSize;
                
                // Find corresponding local file (if it exists)
                var localFile = localFiles.FirstOrDefault(f => 
                    string.Equals(f.RelativePath, downloadAction.FileName, StringComparison.OrdinalIgnoreCase));

                if (localFile != null && downloadAction.RemoteFile != null)
                {
                    try
                    {
                        var localFilePath = Path.Combine(_folders[folderId].Path, localFile.RelativePath);
                        
                        if (!File.Exists(localFilePath))
                        {
                            _logger.LogDebug("Local file {FileName} not found - downloading entire file", downloadAction.FileName);
                            downloadAction.OptimizedSize = downloadAction.FileSize;
                            totalOptimizedSize += downloadAction.FileSize;
                            continue;
                        }
                        
                        // Use Syncthing block comparison for optimization
                        var fileDiff = await _blockDuplicationDetector.CompareFilesAsync(
                            localFilePath, 
                            $"remote:{downloadAction.FileName}", // Placeholder for remote file
                            _cancellationTokenSource.Token);
                        
                        if (fileDiff.Error != null)
                        {
                            _logger.LogWarning("Error comparing files for {FileName}: {Error}", downloadAction.FileName, fileDiff.Error);
                            downloadAction.OptimizedSize = downloadAction.FileSize;
                            totalOptimizedSize += downloadAction.FileSize;
                            continue;
                        }
                        
                        // Calculate transfer savings based on reusable blocks
                        var transferSize = fileDiff.TotalBytesToTransfer;
                        var reusedSize = fileDiff.TotalBytesReused;
                        
                        downloadAction.OptimizedSize = transferSize;
                        downloadAction.SyncthingBlockDiff = fileDiff;
                        totalOptimizedSize += transferSize;
                        totalBlocksDeduped += reusedSize;
                        
                        var savingsPercentage = downloadAction.FileSize > 0 ? 
                            (downloadAction.FileSize - transferSize) * 100.0 / downloadAction.FileSize : 0;
                        
                        _logger.LogInformation("Syncthing block optimization for {FileName}: {ReusableBlocks}/{TotalBlocks} blocks reusable, " +
                                             "transfer {TransferSize}/{TotalSize} bytes ({Savings:F1}% savings)",
                            downloadAction.FileName, fileDiff.ReusableBlocks.Count, fileDiff.TargetBlocks.Count,
                            transferSize, downloadAction.FileSize, savingsPercentage);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error creating Syncthing optimization plan for {FileName}, falling back to full download", 
                            downloadAction.FileName);
                        downloadAction.OptimizedSize = downloadAction.FileSize;
                        totalOptimizedSize += downloadAction.FileSize;
                    }
                }
                else
                {
                    _logger.LogDebug("No local file for comparison: {FileName} - downloading entire file", 
                        downloadAction.FileName);
                    downloadAction.OptimizedSize = downloadAction.FileSize;
                    totalOptimizedSize += downloadAction.FileSize;
                }
            }

            var savedBytes = totalOriginalSize - totalOptimizedSize;
            var savedPercentage = totalOriginalSize > 0 ? (savedBytes * 100.0 / totalOriginalSize) : 0;

            _statistics.TotalBytesDeduped += savedBytes;
            _statistics.TotalBlocksDeduped += (int)Math.Min(totalBlocksDeduped, int.MaxValue);

            _logger.LogInformation("Syncthing block-level optimization: {SavedBytes} bytes saved ({SavedPercentage:F1}%) " +
                                 "out of {TotalBytes} total bytes, {TotalBlocksDeduped} bytes deduplicated",
                savedBytes, savedPercentage, totalOriginalSize, totalBlocksDeduped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Syncthing optimized transfer plans for folder {FolderId}", folderId);
        }
    }

    /// <summary>
    /// Handles incoming relay connection invitations
    /// </summary>
    private async void OnRelayConnectionReceived(object? sender, RelayConnectionEventArgs e)
    {
        try
        {
            var deviceId = Convert.ToHexString(e.Invitation.From);
            _logger.LogInformation("Received relay connection invitation from device {DeviceId} via {RelayUri}", 
                deviceId, e.RelayUri);

            // Check if we know this device
            if (!_devices.ContainsKey(deviceId))
            {
                _logger.LogWarning("Received relay invitation from unknown device {DeviceId}", deviceId);
                return;
            }

            // Join the session to establish connection
            if (e.RelayClient != null)
            {
                var stream = await e.RelayClient.JoinSessionAsync(e.Invitation);
                if (stream != null)
                {
                    _logger.LogInformation("Successfully established relay connection to device {DeviceId}", deviceId);
                    
                    // Create BEP connection using the relay stream
                    await HandleRelayConnectionAsync(deviceId, stream, false);
                }
                else
                {
                    _logger.LogWarning("Failed to join relay session for device {DeviceId}", deviceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling relay connection invitation");
        }
    }

    /// <summary>
    /// Get relay connection status
    /// </summary>
    public List<RelayInfo> GetRelayStatus()
    {
        return _relayManager?.GetRelayInfo() ?? new List<RelayInfo>();
    }

    /// <summary>
    /// Attempt to connect to a device through relay if direct connection fails
    /// </summary>
    public async Task<bool> TryConnectThroughRelayAsync(string deviceId, TimeSpan timeout)
    {
        if (_relayManager == null)
        {
            _logger.LogDebug("Relay manager not available for device {DeviceId}", deviceId);
            return false;
        }

        try
        {
            var stream = await _relayManager.ConnectThroughRelayAsync(deviceId, timeout);
            if (stream != null)
            {
                _logger.LogInformation("Established relay connection to device {DeviceId}", deviceId);
                
                // Create BEP connection using the relay stream
                await HandleRelayConnectionAsync(deviceId, stream, true);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to device {DeviceId} through relay", deviceId);
        }

        return false;
    }

    /// <summary>
    /// Handle a new relay connection by wrapping it with BEP protocol
    /// </summary>
    private async Task HandleRelayConnectionAsync(string deviceId, Stream relayStream, bool isOutgoing)
    {
        try
        {
            _logger.LogDebug("Setting up BEP protocol over relay stream for device {DeviceId}", deviceId);

            // Create a dummy TcpClient wrapper for relay streams
            var relayWrapper = new RelayStreamWrapper(relayStream);
            
            // Create BEP connection using the relay stream
            var connection = new BepConnection(deviceId, relayWrapper, relayStream, _logger, isOutgoing);
            
            // Register the connection with the protocol handler
            await _protocol.RegisterConnectionAsync(connection);
            
            _logger.LogInformation("Successfully established BEP protocol over relay for device {DeviceId}", deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up BEP protocol over relay for device {DeviceId}", deviceId);
            relayStream?.Dispose();
        }
    }

    private async Task LoadConfigurationAsync()
    {
        try
        {
            // Load devices from config.xml via ConfigurationManager
            var devices = await _configManager.GetAllDevicesAsync();
            foreach (var device in devices)
            {
                _devices.TryAdd(device.DeviceId, device);
            }
            _logger.LogInformation("Loaded {DeviceCount} devices from config.xml", devices.Count);

            // Load folders from config.xml via ConfigurationManager
            var folders = await _configManager.GetAllFoldersAsync();
            foreach (var folder in folders)
            {
                _folders.TryAdd(folder.Id, folder);
                _folderSyncSemaphores.TryAdd(folder.Id, new SemaphoreSlim(1, 1));
                _folderStatuses.TryAdd(folder.Id, new SyncStatus
                {
                    FolderId = folder.Id,
                    State = folder.Paused ? SyncState.Paused : SyncState.Idle
                });

                // Register folder for block serving
                _blockRequestHandler.RegisterFolder(folder);

                // Start watching the folder if not paused
                if (!folder.Paused)
                {
                    _fileWatcher.WatchFolder(folder);
                }
            }
            _logger.LogInformation("Loaded {FolderCount} folders from config.xml", folders.Count);

            // Trigger initial scan for all non-paused folders (background task)
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000); // Allow services to initialize
                foreach (var folder in folders.Where(f => !f.Paused))
                {
                    try
                    {
                        _logger.LogInformation("Starting initial scan for folder {FolderId}", folder.Id);
                        await ScanFolderAsync(folder.Id, deep: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during initial scan of folder {FolderId}", folder.Id);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading configuration from database");
        }
    }

    /// <summary>
    /// Apply configuration changes from ConfigXml to the sync engine.
    /// Compares current state with new config and applies changes incrementally.
    /// Similar to Syncthing's lib/config/config.go CommitConfiguration().
    /// </summary>
    public async Task ApplyConfigurationAsync(ConfigXml config, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Applying configuration changes...");

        try
        {
            // Get current folder and device IDs for comparison
            var currentFolderIds = _folders.Keys.ToHashSet();
            var currentDeviceIds = _devices.Keys.ToHashSet();
            var newFolderIds = config.Folders.Select(f => f.Id).ToHashSet();
            var newDeviceIds = config.Devices.Select(d => d.Id).ToHashSet();

            // 1. Remove folders that are no longer in config
            var foldersToRemove = currentFolderIds.Except(newFolderIds).ToList();
            foreach (var folderId in foldersToRemove)
            {
                _logger.LogInformation("Removing folder {FolderId} (no longer in config)", folderId);
                await RemoveFolderInternalAsync(folderId);
            }

            // 2. Remove devices that are no longer in config (except self)
            var devicesToRemove = currentDeviceIds.Except(newDeviceIds)
                .Where(id => id != DeviceId) // Don't remove self
                .ToList();
            foreach (var deviceId in devicesToRemove)
            {
                _logger.LogInformation("Removing device {DeviceId} (no longer in config)", deviceId);
                RemoveDeviceInternal(deviceId);
            }

            // 3. Add or update devices
            foreach (var xmlDevice in config.Devices)
            {
                if (xmlDevice.Id == DeviceId) continue; // Skip self

                if (_devices.TryGetValue(xmlDevice.Id, out var existingDevice))
                {
                    // Update existing device
                    UpdateDeviceFromXml(existingDevice, xmlDevice);
                    _logger.LogDebug("Updated device {DeviceId} ({Name})", xmlDevice.Id, xmlDevice.Name);
                }
                else
                {
                    // Add new device
                    var newDevice = CreateDeviceFromXml(xmlDevice);
                    _devices[xmlDevice.Id] = newDevice;
                    _logger.LogInformation("Added device {DeviceId} ({Name})", xmlDevice.Id, xmlDevice.Name);

                    // Try to connect to new device
                    if (newDevice.Addresses.Any())
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(1000, cancellationToken);
                            await ConnectToDeviceAsync(newDevice, cancellationToken);
                        }, cancellationToken);
                    }
                }
            }

            // 4. Add or update folders
            foreach (var xmlFolder in config.Folders)
            {
                if (_folders.TryGetValue(xmlFolder.Id, out var existingFolder))
                {
                    // Update existing folder
                    var needsWatcherRestart = UpdateFolderFromXml(existingFolder, xmlFolder);
                    _logger.LogDebug("Updated folder {FolderId} ({Label})", xmlFolder.Id, xmlFolder.Label);

                    // Handle watcher restart if path changed or watcher settings changed
                    if (needsWatcherRestart)
                    {
                        _fileWatcher.StopWatchingFolder(xmlFolder.Id);
                        if (!existingFolder.Paused && existingFolder.FsWatcherEnabled)
                        {
                            _fileWatcher.WatchFolder(existingFolder);
                        }
                    }
                }
                else
                {
                    // Add new folder
                    var newFolder = CreateFolderFromXml(xmlFolder);
                    _folders[xmlFolder.Id] = newFolder;
                    _folderSyncSemaphores.TryAdd(xmlFolder.Id, new SemaphoreSlim(1, 1));
                    _folderStatuses.TryAdd(xmlFolder.Id, new SyncStatus
                    {
                        FolderId = xmlFolder.Id,
                        State = newFolder.Paused ? SyncState.Paused : SyncState.Idle
                    });

                    // Register folder for block serving
                    _blockRequestHandler.RegisterFolder(newFolder);

                    // Create directory if needed
                    if (!Directory.Exists(newFolder.Path))
                    {
                        Directory.CreateDirectory(newFolder.Path);
                    }

                    // Start watching if not paused
                    if (!newFolder.Paused)
                    {
                        _fileWatcher.WatchFolder(newFolder);

                        // Perform initial scan
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(2000, cancellationToken);
                            await ScanFolderAsync(xmlFolder.Id, deep: true);
                        }, cancellationToken);
                    }

                    _logger.LogInformation("Added folder {FolderId} ({Label}) at {Path}",
                        xmlFolder.Id, xmlFolder.Label, xmlFolder.Path);
                }
            }

            _logger.LogInformation("Configuration applied: {FolderCount} folders, {DeviceCount} devices",
                _folders.Count, _devices.Count);

            // Log event
            _eventLogger.LogSystemEvent(EventType.ConfigSaved, "Configuration applied", new
            {
                FolderCount = _folders.Count,
                DeviceCount = _devices.Count,
                FoldersAdded = newFolderIds.Except(currentFolderIds).Count(),
                FoldersRemoved = foldersToRemove.Count,
                DevicesAdded = newDeviceIds.Except(currentDeviceIds).Count(),
                DevicesRemoved = devicesToRemove.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying configuration");
            throw;
        }
    }

    /// <summary>
    /// Reload configuration from the config.xml file.
    /// </summary>
    public async Task ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Reloading configuration from config.xml...");

        var configXml = _configManager.GetCurrentConfig();
        await ApplyConfigurationAsync(configXml, cancellationToken);
    }

    /// <summary>
    /// Remove a folder from the sync engine (internal helper)
    /// </summary>
    private async Task RemoveFolderInternalAsync(string folderId)
    {
        if (_folders.TryRemove(folderId, out var folder))
        {
            // Stop watching
            _fileWatcher.StopWatchingFolder(folderId);

            // Remove status and semaphore
            _folderStatuses.TryRemove(folderId, out _);
            if (_folderSyncSemaphores.TryRemove(folderId, out var semaphore))
            {
                semaphore.Dispose();
            }

            // Dispose and remove cached versioner
            if (_folderVersioners.TryRemove(folderId, out var versioner))
            {
                try { versioner?.Dispose(); } catch { /* ignore disposal errors */ }
            }

            // Remove from config manager
            await _configManager.DeleteFolderAsync(folderId);

            _logger.LogInformation("Removed folder {FolderId} ({Label})", folderId, folder.Label);
        }
    }

    /// <summary>
    /// Remove a device from the sync engine (internal helper)
    /// </summary>
    private void RemoveDeviceInternal(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var device))
        {
            // Disconnect if connected
            device.UpdateConnection(false);
            _logger.LogInformation("Removed device {DeviceId} ({Name})", deviceId, device.DeviceName);
        }
    }

    /// <summary>
    /// Create a SyncDevice from ConfigXmlDevice
    /// </summary>
    private SyncDevice CreateDeviceFromXml(ConfigXmlDevice xmlDevice)
    {
        var device = new SyncDevice(xmlDevice.Id, xmlDevice.Name);
        device.SetCompression(xmlDevice.Compression);
        device.SetPaused(xmlDevice.Paused);
        device.SetBandwidthLimits(xmlDevice.MaxSendKbps, xmlDevice.MaxRecvKbps);
        device.SetIntroducer(xmlDevice.Introducer);
        device.AutoAcceptFolders = xmlDevice.AutoAcceptFolders;

        foreach (var address in xmlDevice.Addresses)
        {
            device.AddAddress(address);
        }

        return device;
    }

    /// <summary>
    /// Update an existing SyncDevice from ConfigXmlDevice
    /// </summary>
    private void UpdateDeviceFromXml(SyncDevice device, ConfigXmlDevice xmlDevice)
    {
        device.UpdateName(xmlDevice.Name);
        device.SetCompression(xmlDevice.Compression);
        device.SetPaused(xmlDevice.Paused);
        device.SetBandwidthLimits(xmlDevice.MaxSendKbps, xmlDevice.MaxRecvKbps);
        device.SetIntroducer(xmlDevice.Introducer);
        device.AutoAcceptFolders = xmlDevice.AutoAcceptFolders;

        // Update addresses
        device.UpdateAddresses(xmlDevice.Addresses);
    }

    /// <summary>
    /// Create a SyncFolder from ConfigXmlFolder
    /// </summary>
    private SyncFolder CreateFolderFromXml(ConfigXmlFolder xmlFolder)
    {
        var folder = new SyncFolder(
            xmlFolder.Id,
            xmlFolder.Label,
            xmlFolder.Path,
            xmlFolder.Type,
            xmlFolder.RescanIntervalS,
            xmlFolder.FsWatcherEnabled,
            xmlFolder.FsWatcherDelayS,
            xmlFolder.IgnorePerms,
            xmlFolder.AutoNormalize,
            $"{xmlFolder.MinDiskFree.Value}{xmlFolder.MinDiskFree.Unit}",
            xmlFolder.CopyOwnershipFromParent,
            xmlFolder.ModTimeWindowS,
            xmlFolder.MaxConflicts,
            xmlFolder.DisableSparseFiles,
            xmlFolder.DisableTempIndexes,
            xmlFolder.Paused,
            xmlFolder.WeakHashThresholdPct,
            xmlFolder.MarkerName,
            xmlFolder.CopyRangeMethod,
            xmlFolder.CaseSensitiveFS,
            xmlFolder.JunctionsAsDirs, // Maps to junctionedAsDirectory constructor parameter
            xmlFolder.SyncOwnership,
            xmlFolder.SendOwnership,
            xmlFolder.SyncXattrs,
            xmlFolder.SendXattrs);

        // Set versioning if configured
        if (xmlFolder.Versioning != null && !string.IsNullOrEmpty(xmlFolder.Versioning.Type))
        {
            folder.SetVersioning(new VersioningConfiguration
            {
                Type = xmlFolder.Versioning.Type,
                Params = xmlFolder.Versioning.Params.ToDictionary(p => p.Key, p => p.Val),
                CleanupIntervalS = xmlFolder.Versioning.CleanupIntervalS,
                FSPath = xmlFolder.Versioning.FsPath,
                FSType = xmlFolder.Versioning.FsType
            });
        }

        // Set pull order
        if (!string.IsNullOrEmpty(xmlFolder.Order))
        {
            folder.SetPullOrder(ParsePullOrder(xmlFolder.Order));
        }

        folder.IgnoreDelete = xmlFolder.IgnoreDelete;

        // Add devices
        foreach (var device in xmlFolder.Devices)
        {
            folder.AddDevice(device.Id);
        }

        return folder;
    }

    /// <summary>
    /// Update an existing SyncFolder from ConfigXmlFolder.
    /// Returns true if file watcher needs restart.
    /// Note: Some properties like Label, Path, Type have private setters and cannot be changed after creation.
    /// </summary>
    private bool UpdateFolderFromXml(SyncFolder folder, ConfigXmlFolder xmlFolder)
    {
        var needsWatcherRestart = folder.Path != xmlFolder.Path ||
                                   folder.FsWatcherEnabled != xmlFolder.FsWatcherEnabled ||
                                   folder.FSWatcherDelayS != xmlFolder.FsWatcherDelayS ||
                                   folder.Paused != xmlFolder.Paused;

        // Note: Label, Path, Type have private setters - cannot be changed after creation
        // If these need to change, the folder should be removed and recreated
        if (folder.Label != xmlFolder.Label || folder.Path != xmlFolder.Path || folder.Type != xmlFolder.Type)
        {
            _logger.LogWarning("Folder {FolderId} has changes to Label/Path/Type which cannot be updated in-place. " +
                "Consider removing and recreating the folder.", folder.Id);
        }

        // Update properties with public setters
        folder.RescanIntervalS = xmlFolder.RescanIntervalS;
        folder.FsWatcherEnabled = xmlFolder.FsWatcherEnabled;
        folder.FSWatcherDelayS = xmlFolder.FsWatcherDelayS;
        folder.IgnorePerms = xmlFolder.IgnorePerms;
        folder.AutoNormalize = xmlFolder.AutoNormalize;
        folder.SetMinDiskFree($"{xmlFolder.MinDiskFree.Value}{xmlFolder.MinDiskFree.Unit}");
        folder.MaxConflicts = xmlFolder.MaxConflicts;
        folder.DisableSparseFiles = xmlFolder.DisableSparseFiles;
        folder.DisableTempIndexes = xmlFolder.DisableTempIndexes;
        folder.SetPaused(xmlFolder.Paused);
        folder.WeakHashThresholdPct = xmlFolder.WeakHashThresholdPct;
        folder.MarkerName = xmlFolder.MarkerName;
        folder.IgnoreDelete = xmlFolder.IgnoreDelete;

        // Update versioning
        if (xmlFolder.Versioning != null && !string.IsNullOrEmpty(xmlFolder.Versioning.Type))
        {
            folder.SetVersioning(new VersioningConfiguration
            {
                Type = xmlFolder.Versioning.Type,
                Params = xmlFolder.Versioning.Params.ToDictionary(p => p.Key, p => p.Val),
                CleanupIntervalS = xmlFolder.Versioning.CleanupIntervalS,
                FSPath = xmlFolder.Versioning.FsPath,
                FSType = xmlFolder.Versioning.FsType
            });
        }
        else
        {
            folder.SetVersioning(new VersioningConfiguration { Type = string.Empty });
        }

        // Update pull order
        if (!string.IsNullOrEmpty(xmlFolder.Order))
        {
            folder.SetPullOrder(ParsePullOrder(xmlFolder.Order));
        }

        // Update folder status if paused state changed
        if (_folderStatuses.TryGetValue(folder.Id, out var status))
        {
            if (folder.Paused && status.State != SyncState.Paused)
            {
                status.State = SyncState.Paused;
            }
            else if (!folder.Paused && status.State == SyncState.Paused)
            {
                status.State = SyncState.Idle;
            }
        }

        // Update device list
        var currentDeviceIds = folder.Devices.ToHashSet();
        var newDeviceIds = xmlFolder.Devices.Select(d => d.Id).ToHashSet();

        foreach (var deviceId in currentDeviceIds.Except(newDeviceIds))
        {
            folder.RemoveDevice(deviceId);
        }
        foreach (var deviceId in newDeviceIds.Except(currentDeviceIds))
        {
            folder.AddDevice(deviceId);
        }

        return needsWatcherRestart;
    }

    /// <summary>
    /// Initialize or generate device certificate and set DeviceID (similar to Syncthing's LoadOrGenerateCertificate)
    /// </summary>
    private async Task InitializeDeviceCertificateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Get platform-specific config directory for certificate storage
            var configDir = _configManager.GetConfigDirectory();
            var certPath = Path.Combine(configDir, "cert.pem");
            var keyPath = Path.Combine(configDir, "key.pem");

            _logger.LogInformation("Loading certificate from {CertPath}", certPath);

            // Try to load existing certificate first
            X509Certificate2? existingCert = null;
            try
            {
                // Try to load from config directory (similar to Syncthing's cert.pem/key.pem)
                existingCert = await _certificateManager.LoadCertificateAsync(certPath, keyPath, null, cancellationToken);
            }
            catch
            {
                // Certificate doesn't exist or can't be loaded
                existingCert = null;
            }
            
            if (existingCert != null)
            {
                // Get certificate info using CertificateManager
                var certInfo = _certificateManager.GetCertificateInfo(existingCert);
                var deviceId = DeviceIdGenerator.GenerateFromCertificate(existingCert);
                
                _logger.LogInformation("Loaded existing device certificate with {Algorithm}", 
                    certInfo.SignatureAlgorithm);
                _logger.LogInformation("Device ID: {DeviceId} (Short: {ShortDeviceId})", 
                    deviceId, DeviceIdGenerator.GetShortDeviceId(deviceId));
                _logger.LogInformation("Certificate expires: {ExpiresAt} (in {Days} days)", 
                    certInfo.ExpiresAt, certInfo.DaysUntilExpiry());
                
                // Check if certificate needs renewal (like Syncthing)
                if (_certificateManager.RequiresRenewal(existingCert, 30))
                {
                    _logger.LogWarning("Device certificate expires in {Days} days - attempting auto-renewal", 
                        certInfo.DaysUntilExpiry());
                    
                    // Try to auto-renew certificate (using CertificateManager)
                    var renewedCert = await _certificateManager.RenewCertificateIfNeededAsync(
                        existingCert,
                        $"syncthing-{Environment.MachineName}",
                        20 * 365, // 20 years like Syncthing
                        30, // renewal threshold
                        cancellationToken);
                    
                    if (renewedCert != null && renewedCert != existingCert)
                    {
                        // Save renewed certificate to config directory
                        await _certificateManager.SaveCertificateAsync(renewedCert, certPath, keyPath, cancellationToken);
                        
                        // Update DeviceID with renewed certificate
                        deviceId = DeviceIdGenerator.GenerateFromCertificate(renewedCert);
                        _configuration.DeviceId = deviceId;
                        
                        _logger.LogInformation("Certificate successfully renewed. New Device ID: {DeviceId}", deviceId);
                        _eventLogger.LogSystemEvent(EventType.CertificateRenewed, 
                            "Device certificate automatically renewed", 
                            new { DeviceId = deviceId, OldExpiryDate = certInfo.ExpiresAt });
                    }
                    else
                    {
                        _eventLogger.LogSystemEvent(EventType.CertificateExpired, 
                            "Device certificate needs renewal", 
                            new { DeviceId = deviceId, DaysUntilExpiry = certInfo.DaysUntilExpiry() });
                    }
                }
                
                // Update configuration with certificate-derived DeviceID
                _configuration.DeviceId = deviceId;

                // Add this device to the device list (Syncthing-compatible)
                EnsureLocalDeviceInList();

                // Log device info event with detailed certificate info
                _eventLogger.LogSystemEvent(EventType.DeviceConnected,
                    "Device certificate loaded with details",
                    new {
                        DeviceId = deviceId,
                        SignatureAlgorithm = certInfo.SignatureAlgorithm.ToString(),
                        KeySize = certInfo.KeySize,
                        ExpiresAt = certInfo.ExpiresAt,
                        DaysUntilExpiry = certInfo.DaysUntilExpiry(),
                        Subject = existingCert.Subject
                    });

                return;
            }

            // Generate new certificate if none exists (similar to Syncthing's GenerateCertificate)
            _logger.LogInformation("Generating new device certificate and key");
            
            var newCert = await _certificateManager.CreateDeviceCertificateAsync(
                $"syncthing-{Environment.MachineName}", 
                20 * 365, // 20 years like Syncthing
                CertificateSignatureAlgorithm.Ed25519, // Ed25519 like Syncthing
                cancellationToken);
            
            if (newCert != null)
            {
                // Save the generated certificate to config directory (like Syncthing cert.pem/key.pem)
                await _certificateManager.SaveCertificateAsync(newCert, certPath, keyPath, cancellationToken);

                var deviceId = DeviceIdGenerator.GenerateFromCertificate(newCert);
                _logger.LogInformation("Generated new device certificate with {Algorithm}",
                    _certificateManager.GetCertificateInfo(newCert).SignatureAlgorithm);
                _logger.LogInformation("Device ID: {DeviceId} (Short: {ShortDeviceId})",
                    deviceId, DeviceIdGenerator.GetShortDeviceId(deviceId));
                _logger.LogInformation("Certificate saved to {CertPath}", certPath);
                
                // Update configuration with certificate-derived DeviceID
                _configuration.DeviceId = deviceId;

                // Add this device to the device list (Syncthing-compatible)
                EnsureLocalDeviceInList();

                // Log device creation event with certificate details
                var certInfo = _certificateManager.GetCertificateInfo(newCert);
                _eventLogger.LogSystemEvent(EventType.DeviceConnected,
                    "New device certificate generated with signature algorithm",
                    new {
                        DeviceId = deviceId,
                        SignatureAlgorithm = certInfo.SignatureAlgorithm.ToString(),
                        KeySize = certInfo.KeySize,
                        ExpiresAt = certInfo.ExpiresAt,
                        Subject = newCert.Subject
                    });
            }
            else
            {
                _logger.LogError("Failed to generate device certificate");
                throw new InvalidOperationException("Cannot initialize sync engine without device certificate");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing device certificate");
            _eventLogger.LogSystemEvent(EventType.Failure,
                "Certificate initialization failed",
                new { Error = ex.Message });
            throw;
        }
    }

    /// <summary>
    /// Ensures the local device is in the device list (Syncthing-compatible behavior)
    /// </summary>
    private void EnsureLocalDeviceInList()
    {
        var localDeviceId = _configuration.DeviceId;
        if (string.IsNullOrEmpty(localDeviceId)) return;

        if (!_devices.ContainsKey(localDeviceId))
        {
            var localDevice = new SyncDevice(localDeviceId, _configuration.DeviceName);
            localDevice.AddAddress("dynamic");
            _devices[localDeviceId] = localDevice;
            _logger.LogDebug("Added local device {DeviceId} to device list", localDeviceId);
        }
        else
        {
            // Update device name if it changed
            _devices[localDeviceId].UpdateName(_configuration.DeviceName);
        }
    }

    /// <summary>
    /// Override local changes to global state (for SendOnly folders)
    /// Syncthing-compatible: POST /rest/db/override
    /// </summary>
    public async Task<bool> OverrideFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            _logger.LogWarning("Cannot override: folder {FolderId} not found", folderId);
            return false;
        }

        // Only SendOnly and Master folders can override
        if (folder.SyncType != SyncFolderType.SendOnly && folder.SyncType != SyncFolderType.Master)
        {
            _logger.LogWarning("Cannot override: folder {FolderId} is type {FolderType}, override only supported for SendOnly/Master",
                folderId, folder.SyncType);
            return false;
        }

        var handler = _syncFolderHandlerFactory.CreateHandler(folder);

        // SendOnlyFolderHandler has OverrideAsync method
        if (handler is Handlers.SendOnlyFolderHandler sendOnlyHandler)
        {
            _logger.LogInformation("Executing override for SendOnly folder {FolderId}", folderId);
            return await sendOnlyHandler.OverrideAsync(folder, cancellationToken);
        }

        // MasterFolderHandler inherits from SendOnlyFolderHandler
        if (handler is Handlers.MasterFolderHandler masterHandler)
        {
            _logger.LogInformation("Executing override for Master folder {FolderId}", folderId);
            return await masterHandler.OverrideAsync(folder, cancellationToken);
        }

        _logger.LogWarning("Handler for folder {FolderId} does not support Override operation", folderId);
        return false;
    }

    /// <summary>
    /// Revert local changes to match global state (for ReceiveOnly folders)
    /// Syncthing-compatible: POST /rest/db/revert
    /// </summary>
    public async Task<bool> RevertFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            _logger.LogWarning("Cannot revert: folder {FolderId} not found", folderId);
            return false;
        }

        // Only ReceiveOnly and Slave folders can revert
        if (folder.SyncType != SyncFolderType.ReceiveOnly && folder.SyncType != SyncFolderType.Slave)
        {
            _logger.LogWarning("Cannot revert: folder {FolderId} is type {FolderType}, revert only supported for ReceiveOnly/Slave",
                folderId, folder.SyncType);
            return false;
        }

        var handler = _syncFolderHandlerFactory.CreateHandler(folder);

        // ReceiveOnlyFolderHandler has RevertAsync method
        if (handler is Handlers.ReceiveOnlyFolderHandler receiveOnlyHandler)
        {
            _logger.LogInformation("Executing revert for ReceiveOnly folder {FolderId}", folderId);
            return await receiveOnlyHandler.RevertAsync(folder, cancellationToken);
        }

        // SlaveFolderHandler inherits from ReceiveOnlyFolderHandler
        if (handler is Handlers.SlaveFolderHandler slaveHandler)
        {
            _logger.LogInformation("Executing revert for Slave folder {FolderId}", folderId);
            return await slaveHandler.RevertAsync(folder, cancellationToken);
        }

        _logger.LogWarning("Handler for folder {FolderId} does not support Revert operation", folderId);
        return false;
    }

    /// <summary>
    /// Get completion status for a device on a folder
    /// Syncthing-compatible: GET /rest/db/completion
    /// </summary>
    public async Task<FolderCompletionStatus> GetCompletionAsync(string folderId, string deviceId, CancellationToken cancellationToken = default)
    {
        var result = new FolderCompletionStatus();

        if (!_folders.TryGetValue(folderId, out var folder))
        {
            _logger.LogWarning("GetCompletion: folder {FolderId} not found", folderId);
            return result;
        }

        try
        {
            // Get all file metadata for the folder
            var allFiles = await _database.FileMetadata.GetAllAsync(folderId);
            var filesList = allFiles.ToList();

            // Calculate global totals
            result.GlobalBytes = filesList.Sum(f => f.Size);
            result.GlobalItems = filesList.Count;

            // Get files that need to be synchronized (not locally changed, not deleted)
            var neededFiles = await _database.FileMetadata.GetNeededFilesAsync(folderId);
            var neededList = neededFiles.ToList();

            result.NeedBytes = neededList.Sum(f => f.Size);
            result.NeedItems = neededList.Count;
            result.NeedDeletes = filesList.Count(f => f.IsDeleted && !f.LocallyChanged);

            // Calculate completion percentage
            if (result.GlobalBytes > 0)
            {
                var completedBytes = result.GlobalBytes - result.NeedBytes;
                result.Completion = (double)completedBytes / result.GlobalBytes * 100;
            }
            else
            {
                result.Completion = 100.0;
            }

            // Get current sequence
            result.Sequence = await _database.FileMetadata.GetGlobalSequenceAsync(folderId);

            // Get remote device state
            if (_devices.TryGetValue(deviceId, out var device))
            {
                result.RemoteState = device.IsConnected ? "syncing" : "disconnected";
            }
            else
            {
                result.RemoteState = "unknown";
            }

            // If folder is in a status map, use that state
            if (_folderStatuses.TryGetValue(folderId, out var status))
            {
                result.RemoteState = status.State.ToString().ToLowerInvariant();
            }

            _logger.LogDebug("Completion for folder {FolderId}, device {DeviceId}: {Completion:F1}% ({NeedItems} items, {NeedBytes} bytes needed)",
                folderId, deviceId, result.Completion, result.NeedItems, result.NeedBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating completion for folder {FolderId}, device {DeviceId}", folderId, deviceId);
        }

        return result;
    }

    /// <summary>
    /// Get list of files that need to be synchronized
    /// Syncthing-compatible: GET /rest/db/need
    /// </summary>
    public async Task<FolderNeedList> GetNeedListAsync(string folderId, int page = 1, int perPage = 100, CancellationToken cancellationToken = default)
    {
        var result = new FolderNeedList
        {
            Page = page,
            PerPage = perPage
        };

        if (!_folders.TryGetValue(folderId, out var folder))
        {
            _logger.LogWarning("GetNeedList: folder {FolderId} not found", folderId);
            return result;
        }

        try
        {
            // Get files that need synchronization with pagination
            var offset = (page - 1) * perPage;
            var neededFiles = await _database.FileMetadata.GetNeededFilesOrderedAsync(
                folderId,
                Domain.Enums.SyncPullOrder.SmallestFirst, // Default to smallest first like Syncthing
                perPage,
                offset);

            var neededList = neededFiles.ToList();

            // Get total count for pagination
            var allNeeded = await _database.FileMetadata.GetNeededFilesAsync(folderId);
            var allNeededList = allNeeded.ToList();
            result.Total = allNeededList.Count;

            // Convert to NeedFile objects
            result.Files = neededList.Select(f => new NeedFile
            {
                Name = f.FileName,
                Size = f.Size,
                ModifiedTime = f.ModifiedTime,
                Type = f.FileType == Domain.Entities.FileType.Directory ? "directory" :
                       f.IsSymlink ? "symlink" : "file",
                Availability = new List<string> { f.ModifiedBy ?? "unknown" }
            }).ToList();

            // Calculate progress
            var totalNeedBytes = allNeededList.Sum(f => f.Size);
            var completedBytes = 0L; // Would need to track in-progress downloads

            result.Progress = new SyncProgress
            {
                BytesTotal = totalNeedBytes,
                BytesDone = completedBytes
            };

            _logger.LogDebug("NeedList for folder {FolderId}: page {Page}, {Count}/{Total} items",
                folderId, page, result.Files.Count, result.Total);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting need list for folder {FolderId}", folderId);
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cancellationTokenSource.Cancel(); } catch (ObjectDisposedException) { }
        _statusTimer?.Dispose();
        _fileWatcher?.Dispose();
        _protocol?.Dispose();
        _discovery?.Dispose();
        _relayManager?.Dispose();
        _natService?.Dispose();
        _database?.Dispose();

        // Dispose all cached versioners
        foreach (var versioner in _folderVersioners.Values)
        {
            try { versioner?.Dispose(); } catch { /* ignore disposal errors */ }
        }
        _folderVersioners.Clear();

        try { _cancellationTokenSource.Dispose(); } catch (ObjectDisposedException) { }
    }

    private bool _disposed;
}