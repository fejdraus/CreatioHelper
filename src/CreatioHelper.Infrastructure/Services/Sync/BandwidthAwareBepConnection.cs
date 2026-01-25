using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Network;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Wrapper for BEP connections that applies bandwidth throttling and priority management
/// </summary>
public class BandwidthAwareBepConnection : IBepConnection
{
    private readonly IBepConnection _innerConnection;
    private readonly IBandwidthManager _bandwidthManager;
    private readonly IPriorityManager _priorityManager;
    private readonly ILogger<BandwidthAwareBepConnection> _logger;

    public string DeviceId => _innerConnection.DeviceId;
    public bool IsConnected => _innerConnection.IsConnected;

    public BandwidthAwareBepConnection(
        IBepConnection innerConnection,
        IBandwidthManager bandwidthManager,
        IPriorityManager priorityManager,
        ILogger<BandwidthAwareBepConnection> logger)
    {
        _innerConnection = innerConnection;
        _bandwidthManager = bandwidthManager;
        _priorityManager = priorityManager;
        _logger = logger;
    }

    public async Task SendIndexAsync(string folderId, IEnumerable<object> files)
    {
        if (!ShouldApplyBandwidthLimits())
        {
            await _innerConnection.SendIndexAsync(folderId, files);
            return;
        }

        await _priorityManager.ExecuteWithPriorityAsync(DeviceId, OperationType.MetadataSync, async () =>
        {
            await _innerConnection.SendIndexAsync(folderId, files);
            
            // Estimate index size for throttling (rough approximation)
            var estimatedSize = files.Count() * 200; // ~200 bytes per file entry
            await _bandwidthManager.ThrottleSendAsync(DeviceId, estimatedSize);
        });
    }

    public async Task SendBlockRequestAsync(string folderId, string fileName, long offset, int size, byte[]? hash = null)
    {
        if (!ShouldApplyBandwidthLimits())
        {
            await _innerConnection.SendBlockRequestAsync(folderId, fileName, offset, size, hash);
            return;
        }

        await _priorityManager.ExecuteWithPriorityAsync(DeviceId, OperationType.BlockRequests, async () =>
        {
            await _innerConnection.SendBlockRequestAsync(folderId, fileName, offset, size, hash);
            
            // Block request is small, minimal throttling
            await _bandwidthManager.ThrottleSendAsync(DeviceId, 100);
        });
    }

    public async Task SendBlockResponseAsync(int requestId, byte[] data, Application.Interfaces.BepErrorCode errorCode = Application.Interfaces.BepErrorCode.NoError)
    {
        if (!ShouldApplyBandwidthLimits())
        {
            await _innerConnection.SendBlockResponseAsync(requestId, data, errorCode);
            return;
        }

        await _priorityManager.ExecuteWithPriorityAsync(DeviceId, OperationType.FileTransfer, async () =>
        {
            await _innerConnection.SendBlockResponseAsync(requestId, data, errorCode);
            
            // Throttle actual file data transfer
            if (data != null)
            {
                await _bandwidthManager.ThrottleSendAsync(DeviceId, data.Length);
            }
        });
    }

    public async Task SendPingAsync()
    {
        if (!ShouldApplyBandwidthLimits())
        {
            await _innerConnection.SendPingAsync();
            return;
        }

        await _priorityManager.ExecuteWithPriorityAsync(DeviceId, OperationType.Heartbeat, async () =>
        {
            await _innerConnection.SendPingAsync();
            
            // Ping is very small
            await _bandwidthManager.ThrottleSendAsync(DeviceId, 50);
        });
    }

    public async Task SendPongAsync()
    {
        if (!ShouldApplyBandwidthLimits())
        {
            await _innerConnection.SendPongAsync();
            return;
        }

        await _priorityManager.ExecuteWithPriorityAsync(DeviceId, OperationType.Heartbeat, async () =>
        {
            await _innerConnection.SendPongAsync();
            
            // Pong is very small
            await _bandwidthManager.ThrottleSendAsync(DeviceId, 50);
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _innerConnection.StartAsync(cancellationToken);
    }

    public Task StopAsync()
    {
        return _innerConnection.StopAsync();
    }

    // Event forwarding

    /// <inheritdoc />
    public event EventHandler<BepClusterConfigReceivedEventArgs>? ClusterConfigReceived
    {
        add => _innerConnection.ClusterConfigReceived += value;
        remove => _innerConnection.ClusterConfigReceived -= value;
    }

    public event EventHandler<BepIndexReceivedEventArgs>? IndexReceived
    {
        add => _innerConnection.IndexReceived += value;
        remove => _innerConnection.IndexReceived -= value;
    }

    /// <inheritdoc />
    public event EventHandler<BepIndexUpdateReceivedEventArgs>? IndexUpdateReceived
    {
        add => _innerConnection.IndexUpdateReceived += value;
        remove => _innerConnection.IndexUpdateReceived -= value;
    }

    public event EventHandler<BepBlockRequestReceivedEventArgs>? BlockRequestReceived
    {
        add => _innerConnection.BlockRequestReceived += value;
        remove => _innerConnection.BlockRequestReceived -= value;
    }

    public event EventHandler<BepBlockResponseReceivedEventArgs>? BlockResponseReceived
    {
        add => _innerConnection.BlockResponseReceived += value;
        remove => _innerConnection.BlockResponseReceived -= value;
    }

    /// <inheritdoc />
    public event EventHandler<BepDownloadProgressReceivedEventArgs>? DownloadProgressReceived
    {
        add => _innerConnection.DownloadProgressReceived += value;
        remove => _innerConnection.DownloadProgressReceived -= value;
    }

    public event EventHandler<BepPingReceivedEventArgs>? PingReceived
    {
        add => _innerConnection.PingReceived += value;
        remove => _innerConnection.PingReceived -= value;
    }

    public event EventHandler<BepPongReceivedEventArgs>? PongReceived
    {
        add => _innerConnection.PongReceived += value;
        remove => _innerConnection.PongReceived -= value;
    }

    public event EventHandler<BepConnectionClosedEventArgs>? ConnectionClosed
    {
        add => _innerConnection.ConnectionClosed += value;
        remove => _innerConnection.ConnectionClosed -= value;
    }

    private bool ShouldApplyBandwidthLimits()
    {
        // For now, assume all connections might need throttling
        // In a real implementation, you would check if this is a LAN connection
        return _bandwidthManager.ShouldApplyBandwidthLimits(DeviceId, isLanConnection: false);
    }

    public void Dispose()
    {
        _innerConnection?.Dispose();
    }
}

/// <summary>
/// Factory for creating bandwidth-aware BEP connections
/// </summary>
public class BandwidthAwareBepConnectionFactory
{
    private readonly IBandwidthManager _bandwidthManager;
    private readonly IPriorityManager _priorityManager;
    private readonly ILogger<BandwidthAwareBepConnection> _logger;

    public BandwidthAwareBepConnectionFactory(
        IBandwidthManager bandwidthManager,
        IPriorityManager priorityManager,
        ILogger<BandwidthAwareBepConnection> logger)
    {
        _bandwidthManager = bandwidthManager;
        _priorityManager = priorityManager;
        _logger = logger;
    }

    public BandwidthAwareBepConnection CreateConnection(IBepConnection innerConnection)
    {
        return new BandwidthAwareBepConnection(innerConnection, _bandwidthManager, _priorityManager, _logger);
    }
}