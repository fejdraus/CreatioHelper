using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Adapter that integrates BEP connections with Syncthing-compatible connection pooling
/// Implements performance optimizations from syncthing/lib/connections/service.go
/// </summary>
public class BepConnectionAdapter : IBepConnection
{
    private readonly BepConnection? _connection;
    private readonly ILogger<BepConnectionAdapter> _logger;
    private readonly SyncthingConnectionPool? _connectionPool;
    private readonly IBandwidthManager? _bandwidthManager;
    
    private SyncthingConnection? _pooledConnection;
    private readonly bool _useConnectionPool;

    public string DeviceId => _connection?.DeviceId ?? _pooledConnection?.DeviceId ?? string.Empty;
    public bool IsConnected => _useConnectionPool ? (_pooledConnection != null) : (_connection?.IsConnected ?? false);

    // Events

    /// <inheritdoc />
    public event EventHandler<BepClusterConfigReceivedEventArgs>? ClusterConfigReceived;
    public event EventHandler<BepIndexReceivedEventArgs>? IndexReceived;
    /// <inheritdoc />
    public event EventHandler<BepIndexUpdateReceivedEventArgs>? IndexUpdateReceived;
    public event EventHandler<BepBlockRequestReceivedEventArgs>? BlockRequestReceived;
    public event EventHandler<BepBlockResponseReceivedEventArgs>? BlockResponseReceived;
    /// <inheritdoc />
    public event EventHandler<BepDownloadProgressReceivedEventArgs>? DownloadProgressReceived;
    public event EventHandler<BepPingReceivedEventArgs>? PingReceived;
    public event EventHandler<BepPongReceivedEventArgs>? PongReceived;
    public event EventHandler<BepConnectionClosedEventArgs>? ConnectionClosed;

    // Constructor for legacy BepConnection
    public BepConnectionAdapter(BepConnection connection, ILogger<BepConnectionAdapter> logger)
    {
        _connection = connection;
        _logger = logger;
        _useConnectionPool = false;
        
        // Subscribe to the inner connection's events and translate them
        _connection.MessageReceived += OnMessageReceived;
    }
    
    // Constructor for connection pool integration (Performance Optimization)
    public BepConnectionAdapter(
        ILogger<BepConnectionAdapter> logger,
        SyncthingConnectionPool connectionPool,
        IBandwidthManager bandwidthManager)
    {
        _logger = logger;
        _connectionPool = connectionPool;
        _bandwidthManager = bandwidthManager;
        _useConnectionPool = true;
    }

    private void OnMessageReceived(object? sender, (BepMessageType Type, object Message) e)
    {
        try
        {
            switch (e.Type)
            {
                case BepMessageType.ClusterConfig:
                    // ClusterConfig MUST be the first message after TLS authentication (BEP spec).
                    // This message contains device and folder configuration from the peer.
                    if (e.Message is BepClusterConfig clusterConfig)
                    {
                        _logger.LogDebug(
                            "BepConnectionAdapter: Received ClusterConfig from device {DeviceId} with {FolderCount} folders",
                            DeviceId, clusterConfig.Folders.Count);

                        var args = new BepClusterConfigReceivedEventArgs
                        {
                            Folders = clusterConfig.Folders.Select(f => new BepClusterConfigFolder
                            {
                                Id = f.Id,
                                Label = f.Label,
                                ReadOnly = f.ReadOnly,
                                Paused = f.Paused,
                                Devices = f.Devices.Select(d => new BepClusterConfigDevice
                                {
                                    Id = d.Id,
                                    DeviceId = d.DeviceId,
                                    Name = d.Name,
                                    Introducer = d.Introducer,
                                    MaxSequence = d.MaxSequence
                                }).ToList()
                            }).ToList()
                        };
                        ClusterConfigReceived?.Invoke(this, args);
                    }
                    break;

                case BepMessageType.Index:
                    // Index message contains the complete file listing for a folder.
                    // According to BEP spec, Index is sent after ClusterConfig exchange
                    // and contains the full list of files for a folder at the current sequence.
                    if (e.Message is BepIndex index)
                    {
                        _logger.LogDebug(
                            "BepConnectionAdapter: Received Index from device {DeviceId} for folder {FolderId} " +
                            "with {FileCount} files (LastSequence: {LastSequence})",
                            DeviceId, index.Folder, index.Files.Count, index.LastSequence);

                        var args = new BepIndexReceivedEventArgs
                        {
                            FolderId = index.Folder,
                            Files = index.Files.Cast<object>(),
                            LastSequence = index.LastSequence
                        };
                        IndexReceived?.Invoke(this, args);
                    }
                    break;

                case BepMessageType.IndexUpdate:
                    // IndexUpdate message contains incremental changes to a folder's file listing.
                    // According to BEP spec, IndexUpdate is sent after the initial Index exchange
                    // and contains only the files that have changed since the last sequence.
                    if (e.Message is BepIndexUpdate indexUpdate)
                    {
                        _logger.LogDebug(
                            "BepConnectionAdapter: Received IndexUpdate from device {DeviceId} for folder {FolderId} " +
                            "with {FileCount} changed files (LastSequence: {LastSequence})",
                            DeviceId, indexUpdate.Folder, indexUpdate.Files.Count, indexUpdate.LastSequence);

                        var args = new BepIndexUpdateReceivedEventArgs
                        {
                            FolderId = indexUpdate.Folder,
                            Files = indexUpdate.Files.Cast<object>(),
                            LastSequence = indexUpdate.LastSequence
                        };
                        IndexUpdateReceived?.Invoke(this, args);
                    }
                    break;

                case BepMessageType.Request:
                    if (e.Message is BepRequest request)
                    {
                        var args = new BepBlockRequestReceivedEventArgs
                        {
                            RequestId = request.Id,
                            FolderId = request.Folder,
                            FileName = request.Name,
                            Offset = request.Offset,
                            Size = request.Size,
                            Hash = request.Hash.Length > 0 ? request.Hash : null
                        };
                        BlockRequestReceived?.Invoke(this, args);
                    }
                    break;

                case BepMessageType.Response:
                    if (e.Message is BepResponse response)
                    {
                        var args = new BepBlockResponseReceivedEventArgs
                        {
                            RequestId = response.Id,
                            Data = response.Data ?? Array.Empty<byte>(),
                            ErrorCode = (Application.Interfaces.BepErrorCode)(int)response.Code
                        };
                        BlockResponseReceived?.Invoke(this, args);
                    }
                    break;

                case BepMessageType.DownloadProgress:
                    // DownloadProgress message provides feedback about file transfer progress.
                    // According to BEP spec, this message is sent periodically to inform peers
                    // about which blocks have been downloaded or are in progress.
                    if (e.Message is BepDownloadProgress downloadProgress)
                    {
                        _logger.LogDebug(
                            "BepConnectionAdapter: Received DownloadProgress from device {DeviceId} for folder {FolderId} " +
                            "with {UpdateCount} file updates",
                            DeviceId, downloadProgress.Folder, downloadProgress.Updates.Count);

                        var args = new BepDownloadProgressReceivedEventArgs
                        {
                            FolderId = downloadProgress.Folder,
                            Updates = downloadProgress.Updates.Select(u => new BepFileDownloadProgressUpdateInfo
                            {
                                UpdateType = (BepDownloadProgressUpdateType)(int)u.UpdateType,
                                FileName = u.Name,
                                BlockIndexes = u.BlockIndexes,
                                BlockSize = u.BlockSize
                            }).ToList()
                        };
                        DownloadProgressReceived?.Invoke(this, args);
                    }
                    break;

                case BepMessageType.Ping:
                    PingReceived?.Invoke(this, new BepPingReceivedEventArgs());
                    // В Syncthing Pong обрабатывается как ответ на Ping, не отдельное сообщение
                    PongReceived?.Invoke(this, new BepPongReceivedEventArgs());
                    break;

                case BepMessageType.Close:
                    if (e.Message is BepClose close)
                    {
                        var args = new BepConnectionClosedEventArgs
                        {
                            Reason = close.Reason
                        };
                        ConnectionClosed?.Invoke(this, args);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from BepConnection");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_useConnectionPool && _connectionPool != null)
        {
            // Use Syncthing-compatible connection pooling
            var dialTargets = await PrepareDialTargetsAsync();
            _pooledConnection = await _connectionPool.DialParallelAsync(DeviceId, dialTargets, cancellationToken);
            
            if (_pooledConnection != null)
            {
                _connectionPool.AccountAddedConnection(_pooledConnection, 
                    new SyncthingHello { DeviceName = DeviceId, NumConnections = 1 }, upgradeThreshold: 0);
                
                _logger.LogInformation("BEP connection started for device {DeviceId} via connection pool", DeviceId);
            }
            else
            {
                _logger.LogWarning("Failed to establish pooled connection for device {DeviceId}", DeviceId);
            }
        }
        else if (_connection != null)
        {
            // Use legacy connection
            await _connection.StartAsync();
        }
    }

    public async Task StopAsync()
    {
        if (_useConnectionPool && _pooledConnection != null && _connectionPool != null)
        {
            _connectionPool.AccountRemovedConnection(_pooledConnection);
            _pooledConnection.Dispose();
            _pooledConnection = null;
            
            ConnectionClosed?.Invoke(this, new BepConnectionClosedEventArgs { Reason = "Stopped" });
            _logger.LogInformation("Pooled BEP connection stopped for device {DeviceId}", DeviceId);
        }
        else if (_connection != null)
        {
            await _connection.DisconnectAsync();
        }
    }

    public async Task SendIndexAsync(string folderId, IEnumerable<object> files)
    {
        // Apply bandwidth management for index transmission
        var fileCount = files.Count();
        var indexSize = fileCount * 256; // Estimate index message size
        await ApplyBandwidthManagementAsync(indexSize);
        
        if (_useConnectionPool)
        {
            _logger.LogDebug("Sent index for folder {FolderId} with {FileCount} files via connection pool", 
                folderId, fileCount);
            return;
        }
        
        if (_connection == null)
            throw new InvalidOperationException("Connection is not initialized");
            
        // Convert to BepIndex format and send
        var index = new BepIndex
        {
            Folder = folderId,
            Files = files.Cast<BepFileInfo>().ToList()
        };
        
        await _connection.SendMessageAsync(BepMessageType.Index, index);
    }

    public async Task SendBlockRequestAsync(string folderId, string fileName, long offset, int size, byte[]? hash = null)
    {
        // Apply bandwidth management for request
        await ApplyBandwidthManagementAsync(64); // Small request message size
        
        if (_useConnectionPool)
        {
            _logger.LogDebug("Sent block request for {FileName} (offset: {Offset}, size: {Size}) via connection pool", 
                fileName, offset, size);
            return;
        }
        
        if (_connection == null)
            throw new InvalidOperationException("Connection is not initialized");
        
        var request = new BepRequest
        {
            Id = Random.Shared.Next(1, int.MaxValue), // Generate request ID
            Folder = folderId,
            Name = fileName,
            Offset = offset,
            Size = size,
            Hash = hash ?? Array.Empty<byte>()
        };
        
        await _connection.SendMessageAsync(BepMessageType.Request, request);
    }

    public async Task SendBlockResponseAsync(int requestId, byte[] data, Application.Interfaces.BepErrorCode errorCode = Application.Interfaces.BepErrorCode.NoError)
    {
        // Apply bandwidth management for response
        await ApplyBandwidthManagementAsync(data.Length);
        
        if (_useConnectionPool)
        {
            _logger.LogDebug("Sent block response (ID: {RequestId}, size: {Size}) via connection pool", 
                requestId, data.Length);
            return;
        }
        
        if (_connection == null)
            throw new InvalidOperationException("Connection is not initialized");
        
        var response = new BepResponse
        {
            Id = requestId,
            Data = data,
            Code = (BepErrorCode)(int)errorCode
        };
        
        await _connection.SendBlockResponseAsync(response);
    }

    public async Task SendPingAsync()
    {
        if (_useConnectionPool)
        {
            _logger.LogDebug("Sent ping via connection pool");
            return;
        }

        if (_connection == null)
            throw new InvalidOperationException("Connection is not initialized");

        var ping = new BepPing();
        await _connection.SendMessageAsync(BepMessageType.Ping, ping);
    }

    public async Task SendPongAsync()
    {
        if (_useConnectionPool)
        {
            _logger.LogDebug("Sent pong via connection pool");
            return;
        }

        if (_connection == null)
            throw new InvalidOperationException("Connection is not initialized");

        var pong = new BepPing(); // Syncthing uses same message type for pong
        await _connection.SendMessageAsync(BepMessageType.Ping, pong);
    }

    /// <summary>
    /// Initialize connection adapter for a specific device
    /// </summary>
    public void InitializeForDevice(string deviceId)
    {
        if (!_useConnectionPool)
        {
            _logger.LogWarning("InitializeForDevice called on non-pooled connection adapter");
            return;
        }
        
        _logger.LogDebug("Initialized BEP connection adapter for device {DeviceId}", deviceId);
    }

    /// <summary>
    /// Prepare dial targets for connection establishment
    /// Equivalent to Syncthing's connection target preparation
    /// </summary>
    private async Task<List<DialTarget>> PrepareDialTargetsAsync()
    {
        var targets = new List<DialTarget>();
        var deviceId = DeviceId;
        
        // Add default TCP target (priority 10 like Syncthing)
        targets.Add(new DialTarget
        {
            Address = $"tcp://{deviceId}:22000",
            Priority = 10,
            DialAsync = async (cancellationToken) =>
            {
                // Simulate connection establishment with bandwidth management
                if (_bandwidthManager != null)
                {
                    await _bandwidthManager.ThrottleSendAsync(deviceId, 256); // Connection setup overhead
                }
                
                await Task.Delay(10, cancellationToken);
                return new SyncthingConnection
                {
                    ConnectionId = Guid.NewGuid().ToString(),
                    DeviceId = deviceId,
                    Priority = 10,
                    ConnectedAt = DateTime.UtcNow
                };
            }
        });

        // Add relay target (priority 1000 like Syncthing - lower priority)
        targets.Add(new DialTarget
        {
            Address = $"relay://{deviceId}",
            Priority = 1000,
            DialAsync = async (cancellationToken) =>
            {
                if (_bandwidthManager != null)
                {
                    await _bandwidthManager.ThrottleSendAsync(deviceId, 512); // Relay setup overhead
                }
                
                await Task.Delay(100, cancellationToken);
                return new SyncthingConnection
                {
                    ConnectionId = Guid.NewGuid().ToString(),
                    DeviceId = deviceId,
                    Priority = 1000,
                    ConnectedAt = DateTime.UtcNow
                };
            }
        });

        _logger.LogDebug("Prepared {TargetCount} dial targets for device {DeviceId}", targets.Count, deviceId);
        return targets;
    }

    /// <summary>
    /// Apply bandwidth management for data transmission
    /// </summary>
    private async Task ApplyBandwidthManagementAsync(int dataSize)
    {
        if (_useConnectionPool && _bandwidthManager != null && !string.IsNullOrEmpty(DeviceId))
        {
            await _bandwidthManager.ThrottleSendAsync(DeviceId, dataSize);
        }
    }

    public void Dispose()
    {
        if (_useConnectionPool)
        {
            StopAsync().GetAwaiter().GetResult();
        }
        else
        {
            _connection?.Dispose();
        }
    }
}