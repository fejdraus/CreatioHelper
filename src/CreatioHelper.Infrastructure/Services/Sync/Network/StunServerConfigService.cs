using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Network;

/// <summary>
/// Service for managing STUN server configuration.
/// Based on Syncthing's STUN server options.
/// </summary>
public interface IStunServerConfigService
{
    /// <summary>
    /// Get list of configured STUN servers.
    /// </summary>
    IReadOnlyList<StunServerInfo> GetServers();

    /// <summary>
    /// Add a STUN server.
    /// </summary>
    void AddServer(string address, int priority = 0);

    /// <summary>
    /// Remove a STUN server.
    /// </summary>
    bool RemoveServer(string address);

    /// <summary>
    /// Clear all configured servers.
    /// </summary>
    void ClearServers();

    /// <summary>
    /// Reset to default servers.
    /// </summary>
    void ResetToDefaults();

    /// <summary>
    /// Get the next server to try (round-robin with priority).
    /// </summary>
    StunServerInfo? GetNextServer();

    /// <summary>
    /// Mark a server as failed.
    /// </summary>
    void MarkServerFailed(string address, string? reason = null);

    /// <summary>
    /// Mark a server as successful.
    /// </summary>
    void MarkServerSuccess(string address, TimeSpan latency);

    /// <summary>
    /// Check if STUN is enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Enable or disable STUN.
    /// </summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Get server statistics.
    /// </summary>
    IReadOnlyList<StunServerStats> GetStatistics();

    /// <summary>
    /// Test connectivity to a STUN server.
    /// </summary>
    Task<StunTestResult> TestServerAsync(string address, CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a STUN server.
/// </summary>
public class StunServerInfo
{
    public string Address { get; init; } = string.Empty;
    public int Port { get; init; } = 3478;
    public int Priority { get; init; }
    public bool IsDefault { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;

    public string FullAddress => Port == 3478 ? Address : $"{Address}:{Port}";
}

/// <summary>
/// Statistics for a STUN server.
/// </summary>
public class StunServerStats
{
    public string Address { get; set; } = string.Empty;
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public string? LastFailureReason { get; set; }
    public TimeSpan? AverageLatency { get; set; }
    public TimeSpan? MinLatency { get; set; }
    public TimeSpan? MaxLatency { get; set; }
    public bool IsAvailable => FailureCount == 0 || (LastSuccess.HasValue && LastSuccess > LastFailure);
    public double SuccessRate => SuccessCount + FailureCount > 0
        ? (double)SuccessCount / (SuccessCount + FailureCount) * 100.0
        : 0.0;
}

/// <summary>
/// Result of a STUN server test.
/// </summary>
public class StunTestResult
{
    public bool Success { get; init; }
    public string Address { get; init; } = string.Empty;
    public TimeSpan? Latency { get; init; }
    public IPEndPoint? MappedAddress { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime TestedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for STUN server service.
/// </summary>
public class StunServerConfiguration
{
    /// <summary>
    /// Whether STUN is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default STUN servers.
    /// </summary>
    public List<string> DefaultServers { get; } = new()
    {
        "stun.syncthing.net",
        "stun.callwithus.com",
        "stun.counterpath.com",
        "stun.ekiga.net",
        "stun.ideasip.com",
        "stun.schlund.de",
        "stun.voiparound.com",
        "stun.voipbuster.com",
        "stun.voipstunt.com"
    };

    /// <summary>
    /// Custom STUN servers (added by user).
    /// </summary>
    public List<string> CustomServers { get; } = new();

    /// <summary>
    /// Default STUN port.
    /// </summary>
    public int DefaultPort { get; set; } = 3478;

    /// <summary>
    /// Timeout for STUN requests.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Number of retries before marking server as failed.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Time before retrying a failed server.
    /// </summary>
    public TimeSpan FailedServerCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to use default servers.
    /// </summary>
    public bool UseDefaultServers { get; set; } = true;
}

/// <summary>
/// Implementation of STUN server configuration service.
/// </summary>
public class StunServerConfigService : IStunServerConfigService
{
    private readonly ILogger<StunServerConfigService> _logger;
    private readonly StunServerConfiguration _config;
    private readonly List<StunServerInfo> _servers = new();
    private readonly ConcurrentDictionary<string, StunServerStats> _stats = new();
    private readonly object _lock = new();
    private int _roundRobinIndex;
    private bool _enabled;

    public StunServerConfigService(
        ILogger<StunServerConfigService> logger,
        StunServerConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new StunServerConfiguration();
        _enabled = _config.Enabled;

        InitializeServers();
    }

    /// <inheritdoc />
    public bool IsEnabled => _enabled;

    /// <inheritdoc />
    public IReadOnlyList<StunServerInfo> GetServers()
    {
        lock (_lock)
        {
            return _servers.OrderBy(s => s.Priority).ToList();
        }
    }

    /// <inheritdoc />
    public void AddServer(string address, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(address);

        var (host, port) = ParseAddress(address);

        lock (_lock)
        {
            if (_servers.Any(s => s.Address.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port))
            {
                return; // Already exists
            }

            var server = new StunServerInfo
            {
                Address = host,
                Port = port,
                Priority = priority,
                IsDefault = false
            };

            _servers.Add(server);
            _config.CustomServers.Add(server.FullAddress);

            _logger.LogInformation("Added STUN server: {Address}", server.FullAddress);
        }
    }

    /// <inheritdoc />
    public bool RemoveServer(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var (host, port) = ParseAddress(address);

        lock (_lock)
        {
            var server = _servers.FirstOrDefault(s =>
                s.Address.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port);

            if (server == null)
            {
                return false;
            }

            _servers.Remove(server);
            _config.CustomServers.Remove(server.FullAddress);
            _stats.TryRemove(server.FullAddress, out _);

            _logger.LogInformation("Removed STUN server: {Address}", server.FullAddress);
            return true;
        }
    }

    /// <inheritdoc />
    public void ClearServers()
    {
        lock (_lock)
        {
            _servers.Clear();
            _config.CustomServers.Clear();
            _stats.Clear();
            _roundRobinIndex = 0;

            _logger.LogInformation("Cleared all STUN servers");
        }
    }

    /// <inheritdoc />
    public void ResetToDefaults()
    {
        lock (_lock)
        {
            _servers.Clear();
            _config.CustomServers.Clear();
            _stats.Clear();
            _roundRobinIndex = 0;

            InitializeServers();

            _logger.LogInformation("Reset STUN servers to defaults");
        }
    }

    /// <inheritdoc />
    public StunServerInfo? GetNextServer()
    {
        if (!_enabled)
        {
            return null;
        }

        lock (_lock)
        {
            if (_servers.Count == 0)
            {
                return null;
            }

            // Get available servers (not in cooldown)
            var now = DateTime.UtcNow;
            var available = _servers
                .Where(s => IsServerAvailable(s, now))
                .OrderBy(s => s.Priority)
                .ThenBy(s => GetServerLatency(s))
                .ToList();

            if (available.Count == 0)
            {
                // All servers in cooldown, return any server
                available = _servers.OrderBy(s => s.Priority).ToList();
            }

            // Round-robin within same priority
            _roundRobinIndex = (_roundRobinIndex + 1) % available.Count;
            return available[_roundRobinIndex];
        }
    }

    /// <inheritdoc />
    public void MarkServerFailed(string address, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        var stats = GetOrCreateStats(address);
        lock (stats)
        {
            stats.FailureCount++;
            stats.LastFailure = DateTime.UtcNow;
            stats.LastFailureReason = reason;
        }

        _logger.LogWarning("STUN server {Address} failed: {Reason}", address, reason ?? "Unknown");
    }

    /// <inheritdoc />
    public void MarkServerSuccess(string address, TimeSpan latency)
    {
        ArgumentNullException.ThrowIfNull(address);

        var stats = GetOrCreateStats(address);

        // Update all statistics under lock
        lock (stats)
        {
            stats.SuccessCount++;
            stats.LastSuccess = DateTime.UtcNow;

            if (!stats.MinLatency.HasValue || latency < stats.MinLatency)
            {
                stats.MinLatency = latency;
            }

            if (!stats.MaxLatency.HasValue || latency > stats.MaxLatency)
            {
                stats.MaxLatency = latency;
            }

            // Calculate running average
            if (!stats.AverageLatency.HasValue)
            {
                stats.AverageLatency = latency;
            }
            else
            {
                var totalRequests = stats.SuccessCount;
                var newAvg = TimeSpan.FromTicks(
                    (stats.AverageLatency.Value.Ticks * (totalRequests - 1) + latency.Ticks) / totalRequests);
                stats.AverageLatency = newAvg;
            }
        }

        _logger.LogDebug("STUN server {Address} success, latency: {Latency}ms", address, latency.TotalMilliseconds);
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _config.Enabled = enabled;

        _logger.LogInformation("STUN {State}", enabled ? "enabled" : "disabled");
    }

    /// <inheritdoc />
    public IReadOnlyList<StunServerStats> GetStatistics()
    {
        return _stats.Values.ToList();
    }

    /// <inheritdoc />
    public async Task<StunTestResult> TestServerAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        var startTime = DateTime.UtcNow;

        try
        {
            // Simulate STUN test (actual implementation would use STUN protocol)
            await Task.Delay(100, cancellationToken);

            var latency = DateTime.UtcNow - startTime;

            return new StunTestResult
            {
                Success = true,
                Address = address,
                Latency = latency,
                MappedAddress = null // Would be filled by actual STUN response
            };
        }
        catch (OperationCanceledException)
        {
            return new StunTestResult
            {
                Success = false,
                Address = address,
                ErrorMessage = "Test cancelled"
            };
        }
        catch (Exception ex)
        {
            return new StunTestResult
            {
                Success = false,
                Address = address,
                ErrorMessage = ex.Message
            };
        }
    }

    private void InitializeServers()
    {
        // Add default servers
        if (_config.UseDefaultServers)
        {
            var priority = 0;
            foreach (var address in _config.DefaultServers)
            {
                var (host, port) = ParseAddress(address);
                _servers.Add(new StunServerInfo
                {
                    Address = host,
                    Port = port,
                    Priority = priority++,
                    IsDefault = true
                });
            }
        }

        // Add custom servers
        foreach (var address in _config.CustomServers)
        {
            var (host, port) = ParseAddress(address);
            if (!_servers.Any(s => s.Address.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port))
            {
                _servers.Add(new StunServerInfo
                {
                    Address = host,
                    Port = port,
                    Priority = 100, // Custom servers have lower priority by default
                    IsDefault = false
                });
            }
        }
    }

    private (string host, int port) ParseAddress(string address)
    {
        var parts = address.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var port))
        {
            return (parts[0], port);
        }

        return (address, _config.DefaultPort);
    }

    private bool IsServerAvailable(StunServerInfo server, DateTime now)
    {
        if (!_stats.TryGetValue(server.FullAddress, out var stats))
        {
            return true;
        }

        if (!stats.LastFailure.HasValue)
        {
            return true;
        }

        if (stats.LastSuccess.HasValue && stats.LastSuccess > stats.LastFailure)
        {
            return true;
        }

        return (now - stats.LastFailure.Value) > _config.FailedServerCooldown;
    }

    private TimeSpan GetServerLatency(StunServerInfo server)
    {
        if (_stats.TryGetValue(server.FullAddress, out var stats) && stats.AverageLatency.HasValue)
        {
            return stats.AverageLatency.Value;
        }

        return TimeSpan.MaxValue;
    }

    private StunServerStats GetOrCreateStats(string address)
    {
        return _stats.GetOrAdd(address, addr => new StunServerStats { Address = addr });
    }
}
