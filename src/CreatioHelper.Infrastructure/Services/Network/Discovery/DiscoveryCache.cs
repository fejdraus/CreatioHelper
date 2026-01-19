using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// TTL-based cache for discovered devices and addresses.
/// Based on Syncthing's discovery caching mechanism from lib/discover/cache.go
/// </summary>
public class DiscoveryCache : IDisposable
{
    private readonly ILogger<DiscoveryCache> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _positiveCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _negativeCache = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// TTL for positive cache entries (device found with addresses)
    /// Default: 5 minutes (Syncthing default)
    /// </summary>
    public TimeSpan PositiveTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// TTL for negative cache entries (device not found)
    /// Default: 1 minute (Syncthing default)
    /// </summary>
    public TimeSpan NegativeTtl { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// TTL for local discovery entries
    /// Default: 90 seconds (3 * BroadcastInterval)
    /// </summary>
    public TimeSpan LocalDiscoveryTtl { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Reannounce interval for global discovery
    /// Default: 30 minutes (Syncthing default)
    /// </summary>
    public TimeSpan ReannounceInterval { get; set; } = TimeSpan.FromMinutes(30);

    public DiscoveryCache(ILogger<DiscoveryCache> logger)
    {
        _logger = logger;
        // Run cleanup every minute
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Add a positive entry (device found with addresses)
    /// </summary>
    public void AddPositive(string deviceId, List<string> addresses, DiscoveryCacheSource source)
    {
        var ttl = source == DiscoveryCacheSource.Local ? LocalDiscoveryTtl : PositiveTtl;
        var entry = new CacheEntry
        {
            DeviceId = deviceId,
            Addresses = new List<string>(addresses),
            Source = source,
            ExpiresAt = DateTime.UtcNow.Add(ttl),
            IsNegative = false
        };

        _positiveCache.AddOrUpdate(deviceId, entry, (_, existing) =>
        {
            // Merge addresses from different sources
            var mergedAddresses = new HashSet<string>(existing.Addresses);
            mergedAddresses.UnionWith(addresses);
            entry.Addresses = mergedAddresses.ToList();
            return entry;
        });

        // Remove from negative cache if present
        _negativeCache.TryRemove(deviceId, out _);

        _logger.LogDebug("Added positive cache entry for device {DeviceId} with {Count} addresses, expires in {Ttl}",
            deviceId, addresses.Count, ttl);
    }

    /// <summary>
    /// Add a negative entry (device not found)
    /// </summary>
    public void AddNegative(string deviceId, DiscoveryCacheSource source)
    {
        // Don't override positive entries with negative ones
        if (_positiveCache.ContainsKey(deviceId))
        {
            _logger.LogDebug("Skipping negative cache entry for {DeviceId} - positive entry exists", deviceId);
            return;
        }

        var entry = new CacheEntry
        {
            DeviceId = deviceId,
            Addresses = new List<string>(),
            Source = source,
            ExpiresAt = DateTime.UtcNow.Add(NegativeTtl),
            IsNegative = true
        };

        _negativeCache.TryAdd(deviceId, entry);

        _logger.LogDebug("Added negative cache entry for device {DeviceId}, expires in {Ttl}",
            deviceId, NegativeTtl);
    }

    /// <summary>
    /// Try to get cached addresses for a device
    /// </summary>
    public bool TryGet(string deviceId, out CacheEntry? entry)
    {
        // Check positive cache first
        if (_positiveCache.TryGetValue(deviceId, out entry))
        {
            if (entry.IsValid)
            {
                _logger.LogDebug("Cache hit (positive) for device {DeviceId} with {Count} addresses",
                    deviceId, entry.Addresses.Count);
                return true;
            }
            else
            {
                // Expired - remove it
                _positiveCache.TryRemove(deviceId, out _);
            }
        }

        // Check negative cache
        if (_negativeCache.TryGetValue(deviceId, out entry))
        {
            if (entry.IsValid)
            {
                _logger.LogDebug("Cache hit (negative) for device {DeviceId}", deviceId);
                return true;
            }
            else
            {
                // Expired - remove it
                _negativeCache.TryRemove(deviceId, out _);
            }
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// Get all valid entries
    /// </summary>
    public IEnumerable<CacheEntry> GetAll()
    {
        var now = DateTime.UtcNow;
        return _positiveCache.Values
            .Where(e => e.ExpiresAt > now)
            .ToList();
    }

    /// <summary>
    /// Remove a specific device from cache
    /// </summary>
    public void Remove(string deviceId)
    {
        _positiveCache.TryRemove(deviceId, out _);
        _negativeCache.TryRemove(deviceId, out _);
        _logger.LogDebug("Removed cache entry for device {DeviceId}", deviceId);
    }

    /// <summary>
    /// Clear all cache entries
    /// </summary>
    public void Clear()
    {
        _positiveCache.Clear();
        _negativeCache.Clear();
        _logger.LogDebug("Cache cleared");
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        var now = DateTime.UtcNow;
        return new CacheStatistics
        {
            PositiveEntryCount = _positiveCache.Count(e => e.Value.ExpiresAt > now),
            NegativeEntryCount = _negativeCache.Count(e => e.Value.ExpiresAt > now),
            TotalAddressCount = _positiveCache.Values.Where(e => e.ExpiresAt > now).Sum(e => e.Addresses.Count),
            LocalEntryCount = _positiveCache.Values.Count(e => e.ExpiresAt > now && e.Source == DiscoveryCacheSource.Local),
            GlobalEntryCount = _positiveCache.Values.Count(e => e.ExpiresAt > now && e.Source == DiscoveryCacheSource.Global),
            StaticEntryCount = _positiveCache.Values.Count(e => e.ExpiresAt > now && e.Source == DiscoveryCacheSource.Static)
        };
    }

    private void CleanupExpired(object? state)
    {
        var now = DateTime.UtcNow;
        var removedPositive = 0;
        var removedNegative = 0;

        foreach (var kvp in _positiveCache)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                if (_positiveCache.TryRemove(kvp.Key, out _))
                    removedPositive++;
            }
        }

        foreach (var kvp in _negativeCache)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                if (_negativeCache.TryRemove(kvp.Key, out _))
                    removedNegative++;
            }
        }

        if (removedPositive > 0 || removedNegative > 0)
        {
            _logger.LogDebug("Cache cleanup: removed {PositiveCount} positive and {NegativeCount} negative entries",
                removedPositive, removedNegative);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Cache entry for discovered devices
/// </summary>
public class CacheEntry
{
    public string DeviceId { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public DiscoveryCacheSource Source { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsNegative { get; set; }

    public bool IsValid => ExpiresAt > DateTime.UtcNow;
    public TimeSpan TimeToLive => ExpiresAt - DateTime.UtcNow;
}

/// <summary>
/// Source of cached discovery information
/// </summary>
public enum DiscoveryCacheSource
{
    /// <summary>
    /// Local discovery (LAN broadcast/multicast)
    /// </summary>
    Local,

    /// <summary>
    /// Global discovery (discovery servers)
    /// </summary>
    Global,

    /// <summary>
    /// Static configuration
    /// </summary>
    Static
}

/// <summary>
/// Cache statistics
/// </summary>
public class CacheStatistics
{
    public int PositiveEntryCount { get; set; }
    public int NegativeEntryCount { get; set; }
    public int TotalAddressCount { get; set; }
    public int LocalEntryCount { get; set; }
    public int GlobalEntryCount { get; set; }
    public int StaticEntryCount { get; set; }
}
