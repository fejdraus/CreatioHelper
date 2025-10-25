using System.Collections.Concurrent;
using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Syncthing-compatible rate limiter implementation
/// Equivalent to golang.org/x/time/rate.Limiter
/// </summary>
public class SyncthingRateLimiter
{
    private readonly object _lock = new();
    private double _limit; // bytes per second
    private int _burst; // burst size in bytes
    private double _tokens;
    private DateTime _lastUpdate;

    public const double Infinity = double.PositiveInfinity;
    public const int DefaultBurstSize = 4 * 128 * 1024; // 4 * 128KB like Syncthing

    public SyncthingRateLimiter(double limit, int burst = DefaultBurstSize)
    {
        _limit = limit;
        _burst = burst;
        _tokens = burst;
        _lastUpdate = DateTime.UtcNow;
    }

    public double Limit => _limit;

    public void SetLimit(double newLimit)
    {
        lock (_lock)
        {
            _limit = newLimit;
        }
    }

    public async Task WaitNAsync(int n, CancellationToken cancellationToken = default)
    {
        while (!TryConsume(n))
        {
            var delay = CalculateDelay(n);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private bool TryConsume(int n)
    {
        lock (_lock)
        {
            if (_limit == Infinity)
                return true;

            Advance();
            
            if (_tokens >= n)
            {
                _tokens -= n;
                return true;
            }
            
            return false;
        }
    }

    private void Advance()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastUpdate).TotalSeconds;
        
        if (elapsed > 0 && _limit != Infinity)
        {
            var tokensToAdd = elapsed * _limit;
            _tokens = Math.Min(_burst, _tokens + tokensToAdd);
            _lastUpdate = now;
        }
    }

    private TimeSpan CalculateDelay(int n)
    {
        lock (_lock)
        {
            if (_limit == Infinity)
                return TimeSpan.Zero;
                
            Advance();
            
            if (_tokens >= n)
                return TimeSpan.Zero;
                
            var tokensNeeded = n - _tokens;
            var secondsToWait = tokensNeeded / _limit;
            return TimeSpan.FromSeconds(Math.Max(0.01, secondsToWait));
        }
    }
}

/// <summary>
/// Multi-limiter that applies the most restrictive limit (minimum)
/// Equivalent to Syncthing's totalWaiter
/// </summary>
public class TotalWaiter
{
    private readonly List<SyncthingRateLimiter> _limiters;

    public TotalWaiter(params SyncthingRateLimiter[] limiters)
    {
        _limiters = limiters?.ToList() ?? new List<SyncthingRateLimiter>();
    }

    public double Limit => _limiters.Any() ? _limiters.Min(l => l.Limit) : SyncthingRateLimiter.Infinity;

    public async Task WaitNAsync(int n, CancellationToken cancellationToken = default)
    {
        foreach (var limiter in _limiters)
        {
            await limiter.WaitNAsync(n, cancellationToken);
        }
    }
}

/// <summary>
/// Syncthing-compatible bandwidth manager with exact same behavior as Syncthing's limiter
/// Based on syncthing/lib/connections/limiter.go
/// </summary>
public class SyncthingBandwidthManager : IBandwidthManager
{
    private readonly ILogger<SyncthingBandwidthManager> _logger;
    private readonly object _lock = new();
    private readonly string _myDeviceId;
    
    // Global rate limiters (like Syncthing's lim.write/lim.read)
    private SyncthingRateLimiter _globalWriteLimiter;
    private SyncthingRateLimiter _globalReadLimiter;
    
    // Per-device rate limiters (like Syncthing's deviceWriteLimiters/deviceReadLimiters)
    private readonly ConcurrentDictionary<string, SyncthingRateLimiter> _deviceWriteLimiters = new();
    private readonly ConcurrentDictionary<string, SyncthingRateLimiter> _deviceReadLimiters = new();
    
    private bool _limitBandwidthInLan = true;
    private readonly ConcurrentDictionary<string, BandwidthStats> _stats = new();

    public SyncthingBandwidthManager(ILogger<SyncthingBandwidthManager> logger, string myDeviceId = "LOCAL-DEVICE")
    {
        _logger = logger;
        _myDeviceId = myDeviceId;
        
        // Initialize with unlimited like Syncthing
        _globalWriteLimiter = new SyncthingRateLimiter(SyncthingRateLimiter.Infinity);
        _globalReadLimiter = new SyncthingRateLimiter(SyncthingRateLimiter.Infinity);
    }

    public async Task ThrottleSendAsync(string deviceId, int bytes, CancellationToken cancellationToken = default)
    {
        if (bytes <= 0 || deviceId == _myDeviceId) return;

        var deviceWriteLimiter = GetWriteLimiter(deviceId);
        var totalWaiter = new TotalWaiter(deviceWriteLimiter, _globalWriteLimiter);

        if (totalWaiter.Limit == SyncthingRateLimiter.Infinity) 
            return;

        var stopwatch = Stopwatch.StartNew();
        
        // Syncthing does chunked writes for better burst management
        await WriteInChunksAsync(bytes, totalWaiter, cancellationToken);
        
        stopwatch.Stop();
        UpdateStats(deviceId, bytesSent: bytes, sendTime: stopwatch.Elapsed);

        if (stopwatch.ElapsedMilliseconds > 100)
        {
            _logger.LogDebug("Rate limited send for device {DeviceId}: {Bytes} bytes in {Ms}ms", 
                deviceId, bytes, stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task ThrottleReceiveAsync(string deviceId, int bytes, CancellationToken cancellationToken = default)
    {
        if (bytes <= 0 || deviceId == _myDeviceId) return;

        var deviceReadLimiter = GetReadLimiter(deviceId);
        var totalWaiter = new TotalWaiter(deviceReadLimiter, _globalReadLimiter);

        if (totalWaiter.Limit == SyncthingRateLimiter.Infinity) 
            return;

        var stopwatch = Stopwatch.StartNew();
        
        // For reads, consume all at once (like Syncthing's limitedReader)
        await ConsumeTokensAsync(bytes, totalWaiter, cancellationToken);
        
        stopwatch.Stop();
        UpdateStats(deviceId, bytesReceived: bytes, receiveTime: stopwatch.Elapsed);

        if (stopwatch.ElapsedMilliseconds > 100)
        {
            _logger.LogDebug("Rate limited receive for device {DeviceId}: {Bytes} bytes in {Ms}ms", 
                deviceId, bytes, stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task WriteInChunksAsync(int totalBytes, TotalWaiter waiter, CancellationToken cancellationToken)
    {
        // Syncthing adaptive chunking logic from limitedWriter.Write()
        // "aim for about a write every 10ms"
        var singleWriteSize = Math.Max(1024, (int)(waiter.Limit / 100)); // 10ms worth of data
        singleWriteSize = ((singleWriteSize / 1024) + 1) * 1024; // round up to next KiB
        singleWriteSize = Math.Min(singleWriteSize, SyncthingRateLimiter.DefaultBurstSize);

        var written = 0;
        while (written < totalBytes)
        {
            var toWrite = Math.Min(singleWriteSize, totalBytes - written);
            await waiter.WaitNAsync(toWrite, cancellationToken);
            written += toWrite;
        }
    }

    private async Task ConsumeTokensAsync(int tokens, TotalWaiter waiter, CancellationToken cancellationToken)
    {
        // Handle large reads that exceed burst size (like Syncthing's waiterHolder.take())
        while (tokens > 0)
        {
            var toConsume = Math.Min(tokens, SyncthingRateLimiter.DefaultBurstSize);
            await waiter.WaitNAsync(toConsume, cancellationToken);
            tokens -= toConsume;
        }
    }

    public void UpdateConfiguration(BandwidthConfiguration configuration)
    {
        lock (_lock)
        {
            // Update global limits (like Syncthing's CommitConfiguration)
            var globalSendLimit = configuration.MaxSendKibps <= 0 
                ? SyncthingRateLimiter.Infinity 
                : configuration.MaxSendKibps * 1024.0;
                
            var globalRecvLimit = configuration.MaxRecvKibps <= 0 
                ? SyncthingRateLimiter.Infinity 
                : configuration.MaxRecvKibps * 1024.0;

            _globalWriteLimiter.SetLimit(globalSendLimit);
            _globalReadLimiter.SetLimit(globalRecvLimit);
            _limitBandwidthInLan = configuration.LimitBandwidthInLan;

            // Update per-device limits
            foreach (var device in configuration.DeviceConfigurations ?? new())
            {
                SetDeviceLimits(device.Key, device.Value);
            }

            var limited = globalSendLimit != SyncthingRateLimiter.Infinity || globalRecvLimit != SyncthingRateLimiter.Infinity;
            
            var sendLimitStr = globalSendLimit == SyncthingRateLimiter.Infinity 
                ? "is unlimited" 
                : $"limit is {configuration.MaxSendKibps} KiB/s";
                
            var recvLimitStr = globalRecvLimit == SyncthingRateLimiter.Infinity 
                ? "is unlimited" 
                : $"limit is {configuration.MaxRecvKibps} KiB/s";

            _logger.LogInformation("Overall rate limit in use, send: {Send}, recv: {Recv}", sendLimitStr, recvLimitStr);

            if (limited)
            {
                if (_limitBandwidthInLan)
                {
                    _logger.LogInformation("Rate limits apply to LAN connections");
                }
                else
                {
                    _logger.LogInformation("Rate limits do not apply to LAN connections");
                }
            }
        }
    }

    private void SetDeviceLimits(string deviceId, BandwidthConfiguration.DeviceConfiguration deviceConfig)
    {
        var readLimiter = GetReadLimiter(deviceId);
        var writeLimiter = GetWriteLimiter(deviceId);

        var currentReadLimit = readLimiter.Limit;
        var currentWriteLimit = writeLimiter.Limit;

        var newReadLimit = deviceConfig.MaxRecvKibps <= 0 
            ? SyncthingRateLimiter.Infinity 
            : deviceConfig.MaxRecvKibps * 1024.0;
            
        var newWriteLimit = deviceConfig.MaxSendKibps <= 0 
            ? SyncthingRateLimiter.Infinity 
            : deviceConfig.MaxSendKibps * 1024.0;

        if (Math.Abs(currentReadLimit - newReadLimit) > 0.1 || Math.Abs(currentWriteLimit - newWriteLimit) > 0.1)
        {
            readLimiter.SetLimit(newReadLimit);
            writeLimiter.SetLimit(newWriteLimit);

            var readLimitStr = newReadLimit == SyncthingRateLimiter.Infinity 
                ? "is unlimited" 
                : $"limit is {deviceConfig.MaxRecvKibps} KiB/s";
                
            var writeLimitStr = newWriteLimit == SyncthingRateLimiter.Infinity 
                ? "is unlimited" 
                : $"limit is {deviceConfig.MaxSendKibps} KiB/s";

            _logger.LogInformation("Device is rate limited: {DeviceId}, send: {Send}, recv: {Recv}", 
                deviceId, writeLimitStr, readLimitStr);
        }
    }

    public bool ShouldApplyBandwidthLimits(string deviceId, bool isLanConnection = false)
    {
        if (isLanConnection && !_limitBandwidthInLan)
        {
            return false;
        }

        var deviceWriteLimiter = GetWriteLimiter(deviceId);
        var deviceReadLimiter = GetReadLimiter(deviceId);
        
        return _globalWriteLimiter.Limit != SyncthingRateLimiter.Infinity ||
               _globalReadLimiter.Limit != SyncthingRateLimiter.Infinity ||
               deviceWriteLimiter.Limit != SyncthingRateLimiter.Infinity ||
               deviceReadLimiter.Limit != SyncthingRateLimiter.Infinity;
    }

    public Task<BandwidthStats> GetBandwidthStatsAsync(string deviceId)
    {
        var stats = _stats.GetOrAdd(deviceId, _ => new BandwidthStats 
        { 
            DeviceId = deviceId,
            LastUpdate = DateTime.UtcNow
        });
        
        return Task.FromResult(stats);
    }

    private SyncthingRateLimiter GetReadLimiter(string deviceId)
    {
        return _deviceReadLimiters.GetOrAdd(deviceId, _ => 
            new SyncthingRateLimiter(SyncthingRateLimiter.Infinity));
    }

    private SyncthingRateLimiter GetWriteLimiter(string deviceId)
    {
        return _deviceWriteLimiters.GetOrAdd(deviceId, _ => 
            new SyncthingRateLimiter(SyncthingRateLimiter.Infinity));
    }

    private void UpdateStats(string deviceId, int bytesSent = 0, int bytesReceived = 0, 
        TimeSpan? sendTime = null, TimeSpan? receiveTime = null)
    {
        var stats = _stats.GetOrAdd(deviceId, _ => new BandwidthStats 
        { 
            DeviceId = deviceId,
            LastUpdate = DateTime.UtcNow
        });

        lock (stats)
        {
            stats.BytesSent += bytesSent;
            stats.BytesReceived += bytesReceived;
            
            if (sendTime.HasValue && sendTime.Value.TotalSeconds > 0)
            {
                stats.CurrentSendRate = bytesSent / sendTime.Value.TotalSeconds;
            }
            
            if (receiveTime.HasValue && receiveTime.Value.TotalSeconds > 0)
            {
                stats.CurrentReceiveRate = bytesReceived / receiveTime.Value.TotalSeconds;
            }
            
            stats.LastUpdate = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        // Nothing to dispose in this implementation
    }
}