using System.Collections.Concurrent;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using FolderStats = CreatioHelper.Application.Interfaces.FolderStatistics;
using DeviceStats = CreatioHelper.Application.Interfaces.DeviceStatistics;

namespace CreatioHelper.Infrastructure.Services.Configuration;

/// <summary>
/// Unified configuration manager using config.xml as the single source of truth.
/// Like Syncthing, folder and device configurations are stored in config.xml,
/// while runtime statistics are kept in memory.
/// </summary>
public class ConfigurationManager : IConfigurationManager
{
    private readonly IConfigXmlService _configXmlService;
    private readonly ILogger<ConfigurationManager> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly object _configLock = new();

    // In-memory cache of configuration
    private ConfigXml? _config;
    private readonly ConcurrentDictionary<string, SyncFolder> _folders = new();
    private readonly ConcurrentDictionary<string, SyncDevice> _devices = new();

    // Runtime statistics (not persisted to config.xml)
    private readonly ConcurrentDictionary<string, FolderStats> _folderStats = new();
    private readonly ConcurrentDictionary<string, DeviceStats> _deviceStats = new();

    // Dirty flag for batched saves
    private volatile bool _isDirty;
    private DateTime _lastSaveTime = DateTime.UtcNow;

    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    public bool IsInitialized => _config != null;

    public ConfigurationManager(
        IConfigXmlService configXmlService,
        ILogger<ConfigurationManager> logger)
    {
        _configXmlService = configXmlService;
        _logger = logger;
    }

    #region Initialization

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing ConfigurationManager from config.xml");

        try
        {
            if (_configXmlService.ConfigExists())
            {
                _config = await _configXmlService.LoadAsync(cancellationToken);
            }
            else
            {
                _logger.LogInformation("Config file not found, will be created on first save");
                _config = new ConfigXml
                {
                    Version = 37,
                    Folders = new List<ConfigXmlFolder>(),
                    Devices = new List<ConfigXmlDevice>(),
                    Gui = new ConfigXmlGui
                    {
                        Enabled = true,
                        Address = "127.0.0.1:8384"
                    },
                    Options = new ConfigXmlOptions()
                };
            }

            // Build in-memory caches
            RebuildCaches();

            _logger.LogInformation(
                "ConfigurationManager initialized with {FolderCount} folders and {DeviceCount} devices",
                _folders.Count, _devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ConfigurationManager");
            throw;
        }
    }

    public ConfigXml GetCurrentConfig()
    {
        EnsureInitialized();
        lock (_configLock)
        {
            return _config!;
        }
    }

    private void RebuildCaches()
    {
        _folders.Clear();
        _devices.Clear();

        if (_config == null) return;

        // Build folder cache
        foreach (var folderConfig in _config.Folders)
        {
            var folder = ConvertToSyncFolder(folderConfig);
            _folders[folder.Id] = folder;
        }

        // Build device cache
        foreach (var deviceConfig in _config.Devices)
        {
            var device = ConvertToSyncDevice(deviceConfig);
            _devices[device.DeviceId] = device;
        }
    }

    private void EnsureInitialized()
    {
        if (_config == null)
        {
            throw new InvalidOperationException("ConfigurationManager not initialized. Call InitializeAsync first.");
        }
    }

    #endregion

    #region Folder Operations

    public Task<SyncFolder?> GetFolderAsync(string folderId)
    {
        EnsureInitialized();
        _folders.TryGetValue(folderId, out var folder);
        return Task.FromResult(folder);
    }

    public Task<IReadOnlyList<SyncFolder>> GetAllFoldersAsync()
    {
        EnsureInitialized();
        return Task.FromResult<IReadOnlyList<SyncFolder>>(_folders.Values.ToList());
    }

    public async Task UpsertFolderAsync(SyncFolder folder)
    {
        EnsureInitialized();

        var isNew = !_folders.ContainsKey(folder.Id);
        _folders[folder.Id] = folder;

        // Update config.xml structure
        lock (_configLock)
        {
            var configFolder = ConvertToConfigXmlFolder(folder);
            var existingIndex = _config!.Folders.FindIndex(f => f.Id == folder.Id);

            if (existingIndex >= 0)
            {
                _config.Folders[existingIndex] = configFolder;
            }
            else
            {
                _config.Folders.Add(configFolder);
            }
        }

        _isDirty = true;
        await SaveIfNeededAsync();

        OnConfigurationChanged(new ConfigurationChangedEventArgs
        {
            ChangeType = isNew ? ConfigurationChangeType.FolderAdded : ConfigurationChangeType.FolderUpdated,
            FolderId = folder.Id
        });

        _logger.LogInformation("Folder {FolderId} ({Label}) {Action}",
            folder.Id, folder.Label, isNew ? "added" : "updated");
    }

    public async Task DeleteFolderAsync(string folderId)
    {
        EnsureInitialized();

        if (!_folders.TryRemove(folderId, out _))
        {
            _logger.LogWarning("Folder {FolderId} not found for deletion", folderId);
            return;
        }

        lock (_configLock)
        {
            _config!.Folders.RemoveAll(f => f.Id == folderId);
        }

        _folderStats.TryRemove(folderId, out _);
        _isDirty = true;
        await SaveIfNeededAsync();

        OnConfigurationChanged(new ConfigurationChangedEventArgs
        {
            ChangeType = ConfigurationChangeType.FolderRemoved,
            FolderId = folderId
        });

        _logger.LogInformation("Folder {FolderId} deleted", folderId);
    }

    public Task<IReadOnlyList<SyncFolder>> GetFoldersSharedWithDeviceAsync(string deviceId)
    {
        EnsureInitialized();
        var folders = _folders.Values
            .Where(f => f.Devices.Contains(deviceId))
            .ToList();
        return Task.FromResult<IReadOnlyList<SyncFolder>>(folders);
    }

    public void UpdateFolderStatistics(string folderId, long fileCount, long totalSize, DateTime? lastScan)
    {
        var stats = _folderStats.GetOrAdd(folderId, id => new FolderStats { FolderId = id });
        stats.FileCount = fileCount;
        stats.TotalSize = totalSize;
        stats.LastScan = lastScan;
        stats.LastUpdated = DateTime.UtcNow;
    }

    public FolderStats? GetFolderStatistics(string folderId)
    {
        _folderStats.TryGetValue(folderId, out var stats);
        return stats;
    }

    #endregion

    #region Device Operations

    public Task<SyncDevice?> GetDeviceAsync(string deviceId)
    {
        EnsureInitialized();
        _devices.TryGetValue(deviceId, out var device);
        return Task.FromResult(device);
    }

    public Task<IReadOnlyList<SyncDevice>> GetAllDevicesAsync()
    {
        EnsureInitialized();
        return Task.FromResult<IReadOnlyList<SyncDevice>>(_devices.Values.ToList());
    }

    public async Task UpsertDeviceAsync(SyncDevice device)
    {
        EnsureInitialized();

        var isNew = !_devices.ContainsKey(device.DeviceId);
        _devices[device.DeviceId] = device;

        // Update config.xml structure
        lock (_configLock)
        {
            var configDevice = ConvertToConfigXmlDevice(device);
            var existingIndex = _config!.Devices.FindIndex(d => d.Id == device.DeviceId);

            if (existingIndex >= 0)
            {
                _config.Devices[existingIndex] = configDevice;
            }
            else
            {
                _config.Devices.Add(configDevice);
            }
        }

        _isDirty = true;
        await SaveIfNeededAsync();

        OnConfigurationChanged(new ConfigurationChangedEventArgs
        {
            ChangeType = isNew ? ConfigurationChangeType.DeviceAdded : ConfigurationChangeType.DeviceUpdated,
            DeviceId = device.DeviceId
        });

        _logger.LogInformation("Device {DeviceId} ({Name}) {Action}",
            device.DeviceId, device.DeviceName, isNew ? "added" : "updated");
    }

    public async Task DeleteDeviceAsync(string deviceId)
    {
        EnsureInitialized();

        if (!_devices.TryRemove(deviceId, out _))
        {
            _logger.LogWarning("Device {DeviceId} not found for deletion", deviceId);
            return;
        }

        // Remove device from all folders
        foreach (var folder in _folders.Values)
        {
            folder.Devices.Remove(deviceId);
        }

        lock (_configLock)
        {
            _config!.Devices.RemoveAll(d => d.Id == deviceId);

            // Also remove from folder device lists
            foreach (var folder in _config.Folders)
            {
                folder.Devices.RemoveAll(d => d.Id == deviceId);
            }
        }

        _deviceStats.TryRemove(deviceId, out _);
        _isDirty = true;
        await SaveIfNeededAsync();

        OnConfigurationChanged(new ConfigurationChangedEventArgs
        {
            ChangeType = ConfigurationChangeType.DeviceRemoved,
            DeviceId = deviceId
        });

        _logger.LogInformation("Device {DeviceId} deleted", deviceId);
    }

    public Task<IReadOnlyList<SyncDevice>> GetDevicesForFolderAsync(string folderId)
    {
        EnsureInitialized();

        if (!_folders.TryGetValue(folderId, out var folder))
        {
            return Task.FromResult<IReadOnlyList<SyncDevice>>(Array.Empty<SyncDevice>());
        }

        var devices = folder.Devices
            .Select(deviceId => _devices.TryGetValue(deviceId, out var d) ? d : null)
            .Where(d => d != null)
            .Cast<SyncDevice>()
            .ToList();

        return Task.FromResult<IReadOnlyList<SyncDevice>>(devices);
    }

    public void UpdateDeviceLastSeen(string deviceId, DateTime lastSeen)
    {
        var stats = _deviceStats.GetOrAdd(deviceId, id => new DeviceStats { DeviceId = id });
        stats.LastSeen = lastSeen;
        stats.LastUpdated = DateTime.UtcNow;

        // Also update the device object
        if (_devices.TryGetValue(deviceId, out var device))
        {
            device.LastSeen = lastSeen;
        }
    }

    public void UpdateDeviceStatistics(string deviceId, long bytesReceived, long bytesSent, DateTime lastActivity)
    {
        var stats = _deviceStats.GetOrAdd(deviceId, id => new DeviceStats { DeviceId = id });
        stats.BytesReceived = bytesReceived;
        stats.BytesSent = bytesSent;
        stats.LastActivity = lastActivity;
        stats.LastUpdated = DateTime.UtcNow;
    }

    public DeviceStats? GetDeviceStatistics(string deviceId)
    {
        _deviceStats.TryGetValue(deviceId, out var stats);
        return stats;
    }

    #endregion

    #region Configuration Operations

    public ConfigXmlGui GetGuiConfig()
    {
        EnsureInitialized();
        lock (_configLock)
        {
            return _config!.Gui;
        }
    }

    public async Task UpdateGuiConfigAsync(ConfigXmlGui gui)
    {
        EnsureInitialized();
        lock (_configLock)
        {
            _config!.Gui = gui;
        }

        _isDirty = true;
        await SaveIfNeededAsync();

        OnConfigurationChanged(new ConfigurationChangedEventArgs
        {
            ChangeType = ConfigurationChangeType.GuiUpdated
        });
    }

    public ConfigXmlOptions GetOptionsConfig()
    {
        EnsureInitialized();
        lock (_configLock)
        {
            return _config!.Options;
        }
    }

    public async Task UpdateOptionsConfigAsync(ConfigXmlOptions options)
    {
        EnsureInitialized();
        lock (_configLock)
        {
            _config!.Options = options;
        }

        _isDirty = true;
        await SaveIfNeededAsync();

        OnConfigurationChanged(new ConfigurationChangedEventArgs
        {
            ChangeType = ConfigurationChangeType.OptionsUpdated
        });
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            lock (_configLock)
            {
                _configXmlService.SaveAsync(_config!, cancellationToken).GetAwaiter().GetResult();
            }

            _isDirty = false;
            _lastSaveTime = DateTime.UtcNow;
            _logger.LogDebug("Configuration saved to config.xml");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Reloading configuration from config.xml");

        var newConfig = await _configXmlService.LoadAsync(cancellationToken);

        lock (_configLock)
        {
            _config = newConfig;
        }

        RebuildCaches();
        _isDirty = false;

        OnConfigurationChanged(new ConfigurationChangedEventArgs
        {
            ChangeType = ConfigurationChangeType.FullReload
        });
    }

    private async Task SaveIfNeededAsync()
    {
        // Debounce saves - don't save more than once per second
        if (_isDirty && (DateTime.UtcNow - _lastSaveTime).TotalSeconds >= 1)
        {
            await SaveAsync();
        }
    }

    #endregion

    #region Conversion Methods

    private static SyncFolder ConvertToSyncFolder(ConfigXmlFolder config)
    {
        var folder = new SyncFolder(
            config.Id,
            config.Label,
            config.Path,
            config.Type,
            config.RescanIntervalS,
            config.FsWatcherEnabled,
            config.FsWatcherDelayS,
            config.IgnorePerms,
            config.AutoNormalize,
            config.MinDiskFree?.ToString() ?? "1%",
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

        // Add devices
        foreach (var device in config.Devices)
        {
            folder.AddDevice(device.Id);
        }

        // Set versioning
        if (config.Versioning != null)
        {
            folder.SetVersioning(new VersioningConfiguration
            {
                Type = config.Versioning.Type,
                Params = config.Versioning.Params?.ToDictionary(p => p.Key, p => p.Val) ?? new Dictionary<string, string>(),
                CleanupIntervalS = config.Versioning.CleanupIntervalS,
                FSPath = config.Versioning.FsPath,
                FSType = config.Versioning.FsType
            });
        }

        return folder;
    }

    private static ConfigXmlFolder ConvertToConfigXmlFolder(SyncFolder folder)
    {
        return new ConfigXmlFolder
        {
            Id = folder.Id,
            Label = folder.Label,
            Path = folder.Path,
            Type = folder.Type,
            RescanIntervalS = folder.RescanIntervalS,
            FsWatcherEnabled = folder.FSWatcherEnabled,
            FsWatcherDelayS = folder.FSWatcherDelayS,
            IgnorePerms = folder.IgnorePerms,
            AutoNormalize = folder.AutoNormalizeUnicode,
            CopyOwnershipFromParent = folder.CopyOwnershipFromParent,
            ModTimeWindowS = folder.ModTimeWindowS,
            MaxConflicts = folder.MaxConflicts,
            DisableSparseFiles = folder.DisableSparseFiles,
            DisableTempIndexes = folder.DisableTempIndexes,
            Paused = folder.Paused,
            WeakHashThresholdPct = folder.WeakHashThresholdPct,
            MarkerName = folder.MarkerName,
            CopyRangeMethod = folder.CopyRangeMethod,
            CaseSensitiveFS = folder.CaseSensitiveFS,
            JunctionsAsDirs = folder.JunctionedAsDirectory,
            SyncOwnership = folder.SyncOwnership,
            SendOwnership = folder.SendOwnership,
            SyncXattrs = folder.SyncXattrs,
            SendXattrs = folder.SendXattrs,
            Devices = folder.Devices.Select(d => new ConfigXmlFolderDevice { Id = d }).ToList(),
            Versioning = folder.Versioning != null ? new ConfigXmlVersioning
            {
                Type = folder.Versioning.Type,
                Params = folder.Versioning.Params?.Select(kvp => new ConfigXmlParam { Key = kvp.Key, Val = kvp.Value }).ToList() ?? new List<ConfigXmlParam>(),
                CleanupIntervalS = folder.Versioning.CleanupIntervalS,
                FsPath = folder.Versioning.FSPath,
                FsType = folder.Versioning.FSType
            } : null
        };
    }

    private static SyncDevice ConvertToSyncDevice(ConfigXmlDevice config)
    {
        var device = new SyncDevice(
            config.Id,
            config.Name,
            config.Compression,
            config.Introducer,
            config.SkipIntroductionRemovals,
            config.IntroducedBy,
            config.Paused,
            config.AutoAcceptFolders,
            config.MaxSendKbps,
            config.MaxRecvKbps,
            config.MaxRequestKiB,
            config.Untrusted,
            config.RemoteGUIPort,
            config.NumConnections,
            config.CertificateName);

        device.Addresses = config.Addresses?.ToList() ?? new List<string> { "dynamic" };

        return device;
    }

    private static ConfigXmlDevice ConvertToConfigXmlDevice(SyncDevice device)
    {
        return new ConfigXmlDevice
        {
            Id = device.DeviceId,
            Name = device.DeviceName,
            Addresses = device.Addresses?.ToList() ?? new List<string> { "dynamic" },
            Compression = device.Compression,
            Introducer = device.Introducer,
            SkipIntroductionRemovals = device.SkipIntroductionRemovals,
            IntroducedBy = device.IntroducedBy,
            Paused = device.Paused,
            AutoAcceptFolders = device.AutoAcceptFolders,
            MaxSendKbps = device.MaxSendKbps,
            MaxRecvKbps = device.MaxRecvKbps,
            MaxRequestKiB = device.MaxRequestKib,
            Untrusted = device.Untrusted,
            RemoteGUIPort = device.RemoteGUIPort,
            NumConnections = device.NumConnections,
            CertificateName = device.CertificateName
        };
    }

    #endregion

    private void OnConfigurationChanged(ConfigurationChangedEventArgs e)
    {
        ConfigurationChanged?.Invoke(this, e);
    }
}
