using System.Collections.Concurrent;
using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Token bucket implementation for rate limiting
/// </summary>
public class TokenBucket
{
    private readonly int _capacity;
    private readonly double _refillRate;
    private double _tokens;
    private DateTime _lastRefill;
    private readonly object _lock = new();

    public TokenBucket(int capacityBytesPerSecond)
    {
        _capacity = capacityBytesPerSecond;
        _refillRate = capacityBytesPerSecond; // tokens per second
        _tokens = _capacity;
        _lastRefill = DateTime.UtcNow;
    }

    public async Task ConsumeAsync(int tokens, CancellationToken cancellationToken = default)
    {
        while (!TryConsume(tokens))
        {
            // Calculate how long to wait
            var waitTime = CalculateWaitTime(tokens);
            if (waitTime > TimeSpan.Zero)
            {
                await Task.Delay(waitTime, cancellationToken);
            }
        }
    }

    private bool TryConsume(int tokens)
    {
        lock (_lock)
        {
            Refill();
            
            if (_tokens >= tokens)
            {
                _tokens -= tokens;
                return true;
            }
            
            return false;
        }
    }

    private void Refill()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastRefill).TotalSeconds;
        
        if (elapsed > 0)
        {
            var tokensToAdd = elapsed * _refillRate;
            _tokens = Math.Min(_capacity, _tokens + tokensToAdd);
            _lastRefill = now;
        }
    }

    private TimeSpan CalculateWaitTime(int tokens)
    {
        lock (_lock)
        {
            Refill();
            
            if (_tokens >= tokens)
                return TimeSpan.Zero;
                
            var tokensNeeded = tokens - _tokens;
            var secondsToWait = tokensNeeded / _refillRate;
            return TimeSpan.FromSeconds(Math.Max(0.1, secondsToWait));
        }
    }
}

/// <summary>
/// Bandwidth manager implementing Syncthing-style rate limiting
/// </summary>
public class BandwidthManager : IBandwidthManager
{
    private readonly ILogger<BandwidthManager> _logger;
    private BandwidthConfiguration _configuration;
    private readonly ConcurrentDictionary<string, TokenBucket> _sendBuckets = new();
    private readonly ConcurrentDictionary<string, TokenBucket> _receiveBuckets = new();
    private readonly ConcurrentDictionary<string, BandwidthStats> _stats = new();
    private readonly Timer _statsTimer;

    public BandwidthManager(ILogger<BandwidthManager> logger, BandwidthConfiguration? configuration = null)
    {
        _logger = logger;
        _configuration = configuration ?? new BandwidthConfiguration();
        
        // Update statistics every 5 seconds
        _statsTimer = new Timer(UpdateStatistics, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public async Task ThrottleSendAsync(string deviceId, int bytes, CancellationToken cancellationToken = default)
    {
        if (bytes <= 0) return;
        
        var limits = GetEffectiveSendLimits(deviceId);
        if (limits <= 0) return; // Unlimited
        
        var bucket = _sendBuckets.GetOrAdd($"send_{deviceId}", _ => new TokenBucket(limits * 1024));
        
        var stopwatch = Stopwatch.StartNew();
        await bucket.ConsumeAsync(bytes, cancellationToken);
        stopwatch.Stop();
        
        // Update statistics
        UpdateStats(deviceId, bytesSent: bytes, sendTime: stopwatch.Elapsed);
        
        if (stopwatch.ElapsedMilliseconds > 100)
        {
            _logger.LogDebug("Throttled send for device {DeviceId}: {Bytes} bytes in {Ms}ms", 
                deviceId, bytes, stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task ThrottleReceiveAsync(string deviceId, int bytes, CancellationToken cancellationToken = default)
    {
        if (bytes <= 0) return;
        
        var limits = GetEffectiveRecvLimits(deviceId);
        if (limits <= 0) return; // Unlimited
        
        var bucket = _receiveBuckets.GetOrAdd($"recv_{deviceId}", _ => new TokenBucket(limits * 1024));
        
        var stopwatch = Stopwatch.StartNew();
        await bucket.ConsumeAsync(bytes, cancellationToken);
        stopwatch.Stop();
        
        // Update statistics
        UpdateStats(deviceId, bytesReceived: bytes, receiveTime: stopwatch.Elapsed);
        
        if (stopwatch.ElapsedMilliseconds > 100)
        {
            _logger.LogDebug("Throttled receive for device {DeviceId}: {Bytes} bytes in {Ms}ms", 
                deviceId, bytes, stopwatch.ElapsedMilliseconds);
        }
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

    public void UpdateConfiguration(BandwidthConfiguration configuration)
    {
        _configuration = configuration;
        
        // Clear existing buckets to pick up new limits
        _sendBuckets.Clear();
        _receiveBuckets.Clear();
        
        _logger.LogInformation("Updated bandwidth configuration: MaxSend={MaxSend} KiB/s, MaxRecv={MaxRecv} KiB/s", 
            configuration.MaxSendKibps, configuration.MaxRecvKibps);
    }

    public bool ShouldApplyBandwidthLimits(string deviceId, bool isLanConnection = false)
    {
        if (isLanConnection && !_configuration.LimitBandwidthInLan)
        {
            return false;
        }
        
        var sendLimits = GetEffectiveSendLimits(deviceId);
        var recvLimits = GetEffectiveRecvLimits(deviceId);
        
        return sendLimits > 0 || recvLimits > 0;
    }

    private int GetEffectiveSendLimits(string deviceId)
    {
        return _configuration.GetEffectiveSendLimits(deviceId);
    }

    private int GetEffectiveRecvLimits(string deviceId)
    {
        return _configuration.GetEffectiveRecvLimits(deviceId);
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

    private void UpdateStatistics(object? state)
    {
        try
        {
            foreach (var stats in _stats.Values)
            {
                lock (stats)
                {
                    // Calculate rolling average rates over the last minute
                    var elapsed = (DateTime.UtcNow - stats.LastUpdate).TotalMinutes;
                    if (elapsed > 0)
                    {
                        stats.AverageSendRate = stats.BytesSent / (elapsed * 60);
                        stats.AverageReceiveRate = stats.BytesReceived / (elapsed * 60);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating bandwidth statistics");
        }
    }

    public void Dispose()
    {
        _statsTimer?.Dispose();
    }
}