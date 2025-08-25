using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using CreatioHelper.Infrastructure.Services.Sync.Relay;

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
    private readonly FileWatcher _fileWatcher;
    private readonly ConflictResolver _conflictResolver;
    private readonly FileComparator _fileComparator;
    private readonly FileDownloader _fileDownloader;
    private readonly BlockRequestHandler _blockRequestHandler;
    private readonly DeltaSyncEngine _deltaSyncEngine;
    private readonly RelayConnectionManager? _relayManager;
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

    public SyncEngine(
        ILogger<SyncEngine> logger,
        ISyncProtocol protocol,
        IDeviceDiscovery discovery,
        FileWatcher fileWatcher,
        ConflictResolver conflictResolver,
        FileComparator fileComparator,
        FileDownloader fileDownloader,
        BlockRequestHandler blockRequestHandler,
        DeltaSyncEngine deltaSyncEngine,
        SyncConfiguration configuration,
        X509Certificate2? clientCertificate = null)
    {
        _logger = logger;
        _protocol = protocol;
        _discovery = discovery;
        _fileWatcher = fileWatcher;
        _conflictResolver = conflictResolver;
        _fileComparator = fileComparator;
        _fileDownloader = fileDownloader;
        _blockRequestHandler = blockRequestHandler;
        _deltaSyncEngine = deltaSyncEngine;
        _configuration = configuration;
        
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

        try
        {
            await _protocol.StartListeningAsync();
            await _discovery.StartAsync(cancellationToken);
            
            // Start relay connections if enabled
            if (_relayManager != null)
            {
                await _relayManager.ConnectToRelaysAsync(_configuration.RelayServers);
            }
            
            _isStarted = true;
            _logger.LogInformation("Sync engine started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start sync engine");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isStarted) return;

        _logger.LogInformation("Stopping sync engine");

        _cancellationTokenSource.Cancel();
        
        await _discovery.StopAsync();
        
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

    public Task<SyncDevice> AddDeviceAsync(string deviceId, string name, string? certificateFingerprint = null, List<string>? addresses = null)
    {
        var device = new SyncDevice(deviceId, name, certificateFingerprint);
        
        if (addresses != null)
        {
            foreach (var address in addresses)
            {
                device.AddAddress(address);
            }
        }

        _devices[deviceId] = device;
        
        _logger.LogInformation("Added device {DeviceId} ({Name})", deviceId, name);

        // Try to connect if we have addresses
        if (device.Addresses.Any())
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // Small delay to allow for setup
                await _protocol.ConnectAsync(device, _cancellationTokenSource.Token);
            });
        }

        return Task.FromResult(device);
    }

    public Task<SyncFolder> AddFolderAsync(string folderId, string label, string path, FolderType type = FolderType.SendReceive)
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

        return Task.FromResult(folder);
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

        // Try to reconnect
        await _protocol.ConnectAsync(device, _cancellationTokenSource.Token);
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
            return;
        }

        _logger.LogInformation("Scanning folder {FolderId} (deep: {Deep})", folderId, deep);

        if (_folderStatuses.TryGetValue(folderId, out var status))
        {
            status.State = SyncState.Scanning;
        }

        try
        {
            var files = await _fileWatcher.ScanFolderAsync(folder);
            folder.UpdateLastScan();

            // Send index to connected devices
            var connectedDevices = folder.Devices
                .Where(d => _protocol.IsConnectedAsync(d.DeviceId).Result)
                .Select(d => d.DeviceId);

            foreach (var deviceId in connectedDevices)
            {
                await _protocol.SendIndexAsync(deviceId, folderId, files);
            }

            if (status != null)
            {
                status.State = SyncState.Idle;
                status.LastScan = DateTime.UtcNow;
                status.LocalFiles = files.Count;
                status.LocalBytes = files.Sum(f => f.Size);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {FolderId}", folderId);
            
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

            // Get current files after sync plan execution
            var files = await _fileWatcher.ScanFolderAsync(folder);
            
            // Send updated index to all connected devices that have access to this folder
            var broadcastTasks = new List<Task>();
            
            foreach (var device in folder.Devices)
            {
                if (await _protocol.IsConnectedAsync(device.DeviceId))
                {
                    _logger.LogDebug("Sending updated Index for folder {FolderId} to device {DeviceId}", folderId, device.DeviceId);
                    broadcastTasks.Add(_protocol.SendIndexAsync(device.DeviceId, folderId, files));
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
        
        // Send cluster config and initial index to newly connected device
        _ = Task.Run(async () =>
        {
            await _protocol.SendClusterConfigAsync(e.Device.DeviceId, _folders.Values.ToList());
            
            // Send Index for each shared folder
            foreach (var folder in _folders.Values)
            {
                if (folder.Devices.Any(d => d.DeviceId == e.Device.DeviceId))
                {
                    await SendFolderIndexAsync(e.Device.DeviceId, folder.FolderId);
                }
            }
        });
    }

    private void OnDeviceDisconnected(object? sender, DeviceDisconnectedEventArgs e)
    {
        _logger.LogInformation("Device {DeviceId} disconnected", e.DeviceId);
        
        if (_devices.TryGetValue(e.DeviceId, out var device))
        {
            device.UpdateConnection(false);
        }
    }

    private async void OnIndexReceived(object? sender, IndexReceivedEventArgs e)
    {
        _logger.LogInformation("Received Index from device {DeviceId} for folder {FolderId} with {FileCount} files", 
            e.DeviceId, e.FolderId, e.Files.Count);

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

        // Check if device is allowed to share this folder
        // TEMPORARILY DISABLED FOR TESTING - TODO: Fix device ID detection
        // if (!folder.Devices.Any(d => d.DeviceId == deviceId))
        // {
        //     _logger.LogWarning("Device {DeviceId} is not authorized for folder {FolderId}", deviceId, folderId);
        //     return;
        // }
        _logger.LogInformation("Device {DeviceId} accessing folder {FolderId} (auth check disabled for testing)", deviceId, folderId);

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

            // Create delta sync plans for files that need downloading
            await CreateDeltaSyncPlansAsync(deviceId, folderId, localFiles, syncPlan.FilesToDownload);

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
        _logger.LogInformation("Executing sync plan for folder {FolderId}", folder.FolderId);

        var syncSummary = new SyncSummary();

        try
        {
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

            // Process uploads (send new/updated files)
            foreach (var uploadAction in syncPlan.FilesToUpload)
            {
                try
                {
                    _logger.LogInformation("Uploading file: {FileName} ({Reason})", 
                        uploadAction.FileName, uploadAction.Reason);

                    // TODO: Implement actual file upload
                    // For now, just log the action
                    _logger.LogInformation("Would upload {FileName} ({FileSize} bytes)", 
                        uploadAction.FileName, uploadAction.FileSize);

                    syncSummary.FilesTransferred++;
                    syncSummary.BytesTransferred += uploadAction.FileSize;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error uploading file {FileName}", uploadAction.FileName);
                    syncSummary.Errors.Add(ex.Message);
                }
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

            // Handle conflicts
            foreach (var conflict in syncPlan.Conflicts)
            {
                try
                {
                    _logger.LogWarning("Handling conflict: {FileName} ({ConflictType})", 
                        conflict.FileName, conflict.ConflictType);

                    // TODO: Implement conflict resolution
                    // For now, just log and count as conflict
                    syncSummary.Conflicts++;

                    ConflictDetected?.Invoke(this, new ConflictDetectedEventArgs(folder.FolderId, conflict.FileName, new List<ConflictVersion>
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

            // Update statistics
            _statistics.TotalFilesReceived += syncSummary.FilesTransferred;
            _statistics.TotalBytesIn += syncSummary.BytesTransferred;

            FolderSynced?.Invoke(this, new FolderSyncedEventArgs(folder.FolderId, syncSummary));

            _logger.LogInformation("Sync plan executed for folder {FolderId}: {FilesTransferred} files, {BytesTransferred} bytes, {Conflicts} conflicts, {Errors} errors",
                folder.FolderId, syncSummary.FilesTransferred, syncSummary.BytesTransferred, syncSummary.Conflicts, syncSummary.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing sync plan for folder {FolderId}", folder.FolderId);
            throw;
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
        
        _logger.LogInformation("Downloading {FileName} to {LocalPath}", downloadAction.FileName, localFilePath);

        var result = await _fileDownloader.DownloadFileAsync(
            deviceId, 
            folder.FolderId, 
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

            using var fileStream = File.Create(localFilePath);
            
            foreach (var block in remoteFile.Blocks)
            {
                var blockData = await _protocol.RequestBlockAsync(deviceId, folder.FolderId, remoteFile.Name, block.Offset, block.Size, block.Hash);
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

        if (_folderStatuses.TryGetValue(e.FolderId, out var status))
        {
            status.LocalFiles = e.Files.Count;
            status.LocalBytes = e.Files.Sum(f => f.Size);
            status.LastScan = DateTime.UtcNow;
        }
    }

    private void UpdateStatistics(object? state)
    {
        // Update runtime statistics
        _statistics.Uptime = DateTime.UtcNow - _statistics.StartTime;
        _statistics.ConnectedDevices = _devices.Values.Count(d => d.IsConnected);
    }

    /// <summary>
    /// Creates delta sync plans for files that need to be downloaded
    /// Uses advanced block comparison to minimize transfer
    /// </summary>
    private Task CreateDeltaSyncPlansAsync(string deviceId, string folderId, List<SyncFileInfo> localFiles, List<FileAction> filesToDownload)
    {
        var planKey = $"{deviceId}:{folderId}";
        
        _logger.LogInformation("Creating delta sync plans for {FileCount} files from device {DeviceId}", 
            filesToDownload.Count, deviceId);

        try
        {
            foreach (var downloadAction in filesToDownload)
            {
                // Find corresponding local file (if it exists)
                var localFile = localFiles.FirstOrDefault(f => 
                    string.Equals(f.RelativePath, downloadAction.FileName, StringComparison.OrdinalIgnoreCase));

                if (localFile != null && localFile.Blocks.Any() && downloadAction.RemoteFile != null && downloadAction.RemoteFile.Blocks.Any())
                {
                    // Create delta sync plan using block comparison
                    var deltaPlan = _configuration.EnableAdvancedDeltaSync 
                        ? _deltaSyncEngine.CreateAdvancedSyncPlan(localFile, downloadAction.RemoteFile)
                        : _deltaSyncEngine.CreateSyncPlan(localFile, downloadAction.RemoteFile);

                    // Store plan for use during download
                    var planKey2 = $"{planKey}:{downloadAction.FileName}";
                    _activeSyncPlans[planKey2] = deltaPlan;

                    var transferPercentage = downloadAction.RemoteFile.Size > 0 
                        ? (deltaPlan.TransferredBytes * 100.0 / downloadAction.RemoteFile.Size) : 0;

                    _logger.LogInformation("Delta sync plan for {FileName}: {RequiredBlocks} blocks to transfer " +
                                         "({TransferredBytes}/{TotalBytes} bytes, {TransferPercentage:F1}%)",
                        downloadAction.FileName, deltaPlan.RequiredBlocks.Count, 
                        deltaPlan.TransferredBytes, downloadAction.RemoteFile.Size, transferPercentage);

                    // Update download action with delta information
                    downloadAction.DeltaSyncPlan = deltaPlan;
                    downloadAction.OptimizedSize = deltaPlan.TransferredBytes;
                }
                else
                {
                    _logger.LogDebug("No delta sync possible for {FileName} - downloading entire file", 
                        downloadAction.FileName);
                }
            }

            var totalOriginalBytes = filesToDownload.Sum(f => f.FileSize);
            var totalOptimizedBytes = filesToDownload.Sum(f => f.OptimizedSize ?? f.FileSize);
            var savedBytes = totalOriginalBytes - totalOptimizedBytes;
            var savedPercentage = totalOriginalBytes > 0 ? (savedBytes * 100.0 / totalOriginalBytes) : 0;

            _logger.LogInformation("Delta sync optimization: {SavedBytes} bytes saved ({SavedPercentage:F1}%) " +
                                 "out of {TotalBytes} total bytes",
                savedBytes, savedPercentage, totalOriginalBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating delta sync plans for folder {FolderId}", folderId);
        }
        
        return Task.CompletedTask;
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

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _statusTimer?.Dispose();
        _fileWatcher?.Dispose();
        _protocol?.Dispose();
        _discovery?.Dispose();
        _relayManager?.Dispose();
        _cancellationTokenSource.Dispose();
    }
}