using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Syncthing-compatible connection pool and management
/// Based on syncthing/lib/connections/service.go connection handling
/// </summary>
public class SyncthingConnectionPool : IDisposable
{
    private readonly ILogger<SyncthingConnectionPool> _logger;
    private readonly ConcurrentDictionary<string, DeviceConnectionTracker> _deviceConnections = new();
    private readonly SyncthingSemaphore _dialSemaphore;
    private readonly SyncthingSemaphore _globalConnectionSemaphore;
    
    // Syncthing constants from service.go
    private const int DialMaxParallel = 64;
    private const int DialMaxParallelPerDevice = 8;
    private const int MaxNumConnections = 128;
    private const int TlsHandshakeTimeoutSeconds = 10;
    private const int MinConnectionLoopSleep = 5;
    private const int StdConnectionLoopSleep = 60;
    private const int ShortLivedConnectionThreshold = 5;
    
    private bool _disposed = false;
    
    public SyncthingConnectionPool(ILogger<SyncthingConnectionPool> logger)
    {
        _logger = logger;
        _dialSemaphore = new SyncthingSemaphore(DialMaxParallel);
        _globalConnectionSemaphore = new SyncthingSemaphore(MaxNumConnections);
    }
    
    /// <summary>
    /// Get device connection tracker (equivalent to Syncthing's deviceConnectionTracker)
    /// </summary>
    public DeviceConnectionTracker GetDeviceTracker(string deviceId)
    {
        return _deviceConnections.GetOrAdd(deviceId, id => new DeviceConnectionTracker(id, _logger));
    }
    
    /// <summary>
    /// Account for added connection (equivalent to Syncthing's accountAddedConnection)
    /// </summary>
    public void AccountAddedConnection(SyncthingConnection connection, SyncthingHello hello, int upgradeThreshold)
    {
        var tracker = GetDeviceTracker(connection.DeviceId);
        tracker.AddConnection(connection, hello, upgradeThreshold);
        
        _logger.LogInformation("Added connection for device {DeviceId} (now {Count}), they want {WantedConnections} connections",
            connection.DeviceId, tracker.ConnectionCount, hello.NumConnections);
    }
    
    /// <summary>
    /// Account for removed connection (equivalent to Syncthing's accountRemovedConnection)
    /// </summary>
    public void AccountRemovedConnection(SyncthingConnection connection)
    {
        var tracker = GetDeviceTracker(connection.DeviceId);
        tracker.RemoveConnection(connection);
        
        _logger.LogInformation("Removed connection for device {DeviceId} (now {Count})",
            connection.DeviceId, tracker.ConnectionCount);
    }
    
    /// <summary>
    /// Dial devices in parallel with priority and semaphore management
    /// Equivalent to Syncthing's dialParallel
    /// </summary>
    public async Task<SyncthingConnection?> DialParallelAsync(string deviceId, 
        List<DialTarget> dialTargets, CancellationToken cancellationToken)
    {
        if (!dialTargets.Any())
            return null;
            
        // Group targets by priority (like Syncthing does)
        var targetBuckets = dialTargets.GroupBy(t => t.Priority)
            .OrderBy(g => g.Key) // Lower priority values = higher priority
            .ToList();
            
        var perDeviceSemaphore = new SyncthingSemaphore(DialMaxParallelPerDevice);
        var multiSemaphore = new SyncthingMultiSemaphore(perDeviceSemaphore, _dialSemaphore);
        
        foreach (var bucket in targetBuckets)
        {
            var targets = bucket.ToList();
            var results = new ConcurrentBag<SyncthingConnection>();
            var tasks = new List<Task>();
            
            foreach (var target in targets)
            {
                tasks.Add(Task.Run(async () => 
                {
                    if (!await multiSemaphore.TakeWithContextAsync(cancellationToken))
                        return;
                        
                    try
                    {
                        var connection = await target.DialAsync(cancellationToken);
                        if (connection != null && ValidateConnection(connection, deviceId))
                        {
                            results.Add(connection);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Dialing {DeviceId} at {Target} failed", deviceId, target.Address);
                    }
                    finally
                    {
                        multiSemaphore.Give();
                    }
                }));
            }
            
            // Wait for first successful connection or all to complete
            var completedTask = await Task.WhenAny(
                Task.Run(async () => 
                {
                    while (results.IsEmpty && tasks.Any(t => !t.IsCompleted))
                    {
                        await Task.Delay(10, cancellationToken);
                    }
                }),
                Task.WhenAll(tasks)
            );
            
            if (!results.IsEmpty)
            {
                var connection = results.First();
                _logger.LogDebug("Connected to {DeviceId} with priority {Priority} using {Connection}", 
                    deviceId, bucket.Key, connection.RemoteEndpoint);
                
                // Close any other connections that came back
                foreach (var extraConn in results.Skip(1))
                {
                    extraConn.Dispose();
                }
                
                return connection;
            }
        }
        
        _logger.LogDebug("Failed to connect to device {DeviceId}", deviceId);
        return null;
    }
    
    /// <summary>
    /// Validate connection identity (equivalent to Syncthing's validateIdentity)
    /// </summary>
    private bool ValidateConnection(SyncthingConnection connection, string expectedDeviceId)
    {
        try
        {
            // Validate certificate and device ID
            if (connection.RemoteCertificate == null)
            {
                _logger.LogWarning("No certificate from remote endpoint {Endpoint}", connection.RemoteEndpoint);
                return false;
            }
            
            var remoteDeviceId = ComputeDeviceId(connection.RemoteCertificate);
            if (remoteDeviceId != expectedDeviceId)
            {
                _logger.LogWarning("Unexpected device ID, expected {Expected} got {Actual}", 
                    expectedDeviceId, remoteDeviceId);
                return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating connection identity");
            return false;
        }
    }
    
    /// <summary>
    /// Compute device ID from certificate (Syncthing-compatible)
    /// </summary>
    private string ComputeDeviceId(X509Certificate2 certificate)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(certificate.RawData);
        return Convert.ToHexString(hash).ToLower();
    }
    
    /// <summary>
    /// Get number of connected devices
    /// </summary>
    public int ConnectedDeviceCount => _deviceConnections.Count(kv => kv.Value.ConnectionCount > 0);
    
    /// <summary>
    /// Get total number of connections
    /// </summary>
    public int TotalConnectionCount => _deviceConnections.Sum(kv => kv.Value.ConnectionCount);
    
    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var tracker in _deviceConnections.Values)
            {
                tracker.Dispose();
            }
            
            _deviceConnections.Clear();
            _dialSemaphore?.SetCapacity(0);
            _globalConnectionSemaphore?.SetCapacity(0);
            
            _disposed = true;
        }
    }
}

/// <summary>
/// Tracks connections for a specific device (equivalent to Syncthing's deviceConnectionTracker)
/// </summary>
public class DeviceConnectionTracker : IDisposable
{
    private readonly string _deviceId;
    private readonly ILogger _logger;
    private readonly List<SyncthingConnection> _connections = new();
    private readonly object _lock = new();
    private int _wantedConnections = 1;
    
    public DeviceConnectionTracker(string deviceId, ILogger logger)
    {
        _deviceId = deviceId;
        _logger = logger;
    }
    
    public int ConnectionCount 
    { 
        get { lock (_lock) return _connections.Count; } 
    }
    
    public int WantedConnections
    {
        get { lock (_lock) return _wantedConnections; }
    }
    
    /// <summary>
    /// Add connection and manage connection limits
    /// </summary>
    public void AddConnection(SyncthingConnection connection, SyncthingHello hello, int upgradeThreshold)
    {
        lock (_lock)
        {
            _connections.Add(connection);
            _wantedConnections = (int)hello.NumConnections;
            
            // Close worse priority connections if needed
            CloseWorsePriorityConnections(connection.Priority - upgradeThreshold);
        }
    }
    
    /// <summary>
    /// Remove connection
    /// </summary>
    public void RemoveConnection(SyncthingConnection connection)
    {
        lock (_lock)
        {
            _connections.RemoveAll(c => c.ConnectionId == connection.ConnectionId);
        }
    }
    
    /// <summary>
    /// Get worst connection priority
    /// </summary>
    public int GetWorstConnectionPriority()
    {
        lock (_lock)
        {
            if (!_connections.Any())
                return int.MaxValue;
                
            return _connections.Max(c => c.Priority);
        }
    }
    
    /// <summary>
    /// Close connections with worse priority than cutoff
    /// </summary>
    private void CloseWorsePriorityConnections(int cutoff)
    {
        var toClose = _connections.Where(c => c.Priority > cutoff).ToList();
        
        foreach (var connection in toClose)
        {
            _logger.LogDebug("Closing connection {ConnectionId} to {DeviceId} with priority {Priority} (cutoff {Cutoff})",
                connection.ConnectionId, _deviceId, connection.Priority, cutoff);
            
            Task.Run(() => connection.Dispose());
        }
    }
    
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var connection in _connections)
            {
                connection.Dispose();
            }
            _connections.Clear();
        }
    }
}

/// <summary>
/// Represents a dial target with priority
/// </summary>
public class DialTarget
{
    public string Address { get; set; } = string.Empty;
    public int Priority { get; set; }
    public Func<CancellationToken, Task<SyncthingConnection?>> DialAsync { get; set; } = _ => Task.FromResult<SyncthingConnection?>(null);
}

/// <summary>
/// Syncthing connection representation
/// </summary>
public class SyncthingConnection : IDisposable
{
    public string ConnectionId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int Priority { get; set; }
    public EndPoint? RemoteEndpoint { get; set; }
    public X509Certificate2? RemoteCertificate { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    
    private bool _disposed = false;
    
    public void Dispose()
    {
        if (!_disposed)
        {
            // Close underlying connection here
            _disposed = true;
        }
    }
}

/// <summary>
/// Syncthing Hello message
/// </summary>
public class SyncthingHello
{
    public string ClientName { get; set; } = "syncthing";
    public string ClientVersion { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    public uint NumConnections { get; set; } = 1;
    public string DeviceName { get; set; } = string.Empty;
}