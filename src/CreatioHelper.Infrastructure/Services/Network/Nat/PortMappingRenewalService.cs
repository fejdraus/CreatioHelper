using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Nat;

/// <summary>
/// Background service for automatic port mapping renewal.
/// Monitors active mappings and renews them before expiration.
/// Based on Syncthing's NAT mapping renewal logic.
/// </summary>
public class PortMappingRenewalService : IDisposable
{
    private readonly ILogger<PortMappingRenewalService> _logger;
    private readonly INatTraversalManager _natManager;
    private readonly PortMappingRenewalConfiguration _config;

    private readonly ConcurrentDictionary<string, MappingRenewalInfo> _trackedMappings = new();
    private Timer? _renewalTimer;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Is the service running
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Event raised when a mapping is successfully renewed
    /// </summary>
    public event EventHandler<MappingRenewedEventArgs>? MappingRenewed;

    /// <summary>
    /// Event raised when a mapping renewal fails
    /// </summary>
    public event EventHandler<MappingRenewalFailedEventArgs>? MappingRenewalFailed;

    /// <summary>
    /// Event raised when a mapping expires
    /// </summary>
    public event EventHandler<MappingExpiredEventArgs>? MappingExpired;

    public PortMappingRenewalService(
        ILogger<PortMappingRenewalService> logger,
        INatTraversalManager natManager,
        PortMappingRenewalConfiguration config)
    {
        _logger = logger;
        _natManager = natManager;
        _config = config;

        // Subscribe to NAT manager events
        _natManager.MappingExpiring += OnMappingExpiring;
    }

    /// <summary>
    /// Start the renewal service
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            return Task.CompletedTask;

        _logger.LogInformation("Starting port mapping renewal service with interval {Interval} minutes",
            _config.CheckIntervalMinutes);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;

        // Start periodic renewal check
        _renewalTimer = new Timer(
            async _ => await CheckAndRenewMappingsAsync(_cancellationTokenSource.Token),
            null,
            TimeSpan.FromMinutes(_config.CheckIntervalMinutes),
            TimeSpan.FromMinutes(_config.CheckIntervalMinutes));

        _logger.LogInformation("Port mapping renewal service started");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the renewal service
    /// </summary>
    public Task StopAsync()
    {
        if (!_isRunning)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping port mapping renewal service");

        _cancellationTokenSource?.Cancel();
        _renewalTimer?.Dispose();
        _renewalTimer = null;
        _isRunning = false;

        _logger.LogInformation("Port mapping renewal service stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Track a mapping for automatic renewal
    /// </summary>
    public void TrackMapping(NatMappingResult mapping)
    {
        var info = new MappingRenewalInfo
        {
            Mapping = mapping,
            AddedAt = DateTime.UtcNow,
            LastRenewal = DateTime.UtcNow,
            RenewalCount = 0,
            FailureCount = 0
        };

        _trackedMappings.AddOrUpdate(mapping.Id, info, (_, _) => info);

        _logger.LogDebug("Now tracking mapping {MappingId} for renewal: {Protocol}:{InternalPort}→{ExternalPort}",
            mapping.Id, mapping.Protocol, mapping.InternalPort, mapping.ExternalPort);
    }

    /// <summary>
    /// Stop tracking a mapping
    /// </summary>
    public void UntrackMapping(string mappingId)
    {
        if (_trackedMappings.TryRemove(mappingId, out var info))
        {
            _logger.LogDebug("Stopped tracking mapping {MappingId}", mappingId);
        }
    }

    /// <summary>
    /// Get renewal status
    /// </summary>
    public PortMappingRenewalStatus GetStatus()
    {
        var now = DateTime.UtcNow;
        return new PortMappingRenewalStatus
        {
            IsRunning = _isRunning,
            TrackedMappingCount = _trackedMappings.Count,
            MappingsNeedingRenewal = _trackedMappings.Values.Count(m => m.Mapping.ShouldRenew),
            ExpiredMappingCount = _trackedMappings.Values.Count(m => m.Mapping.IsExpired),
            TotalRenewals = _trackedMappings.Values.Sum(m => m.RenewalCount),
            TotalFailures = _trackedMappings.Values.Sum(m => m.FailureCount),
            CheckIntervalMinutes = _config.CheckIntervalMinutes,
            Mappings = _trackedMappings.Values.Select(m => new MappingRenewalSummary
            {
                MappingId = m.Mapping.Id,
                Protocol = m.Mapping.Protocol,
                InternalPort = m.Mapping.InternalPort,
                ExternalPort = m.Mapping.ExternalPort,
                ExpiresAt = m.Mapping.ExpiresAt,
                LastRenewal = m.LastRenewal,
                RenewalCount = m.RenewalCount,
                FailureCount = m.FailureCount,
                NeedsRenewal = m.Mapping.ShouldRenew,
                IsExpired = m.Mapping.IsExpired
            }).ToList()
        };
    }

    /// <summary>
    /// Force immediate renewal check
    /// </summary>
    public async Task<int> RenewNowAsync(CancellationToken cancellationToken = default)
    {
        return await CheckAndRenewMappingsAsync(cancellationToken);
    }

    private void OnMappingExpiring(object? sender, MappingExpiringEventArgs e)
    {
        // If we're not already tracking this mapping, start tracking it
        if (!_trackedMappings.ContainsKey(e.Mapping.Id))
        {
            TrackMapping(e.Mapping);
        }

        _logger.LogDebug("Mapping {MappingId} expiring in {TimeRemaining}, scheduling renewal",
            e.Mapping.Id, e.TimeRemaining);
    }

    private async Task<int> CheckAndRenewMappingsAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return 0;

        _logger.LogDebug("Checking {Count} tracked mappings for renewal", _trackedMappings.Count);

        var renewedCount = 0;
        var expiredMappings = new List<string>();

        foreach (var kvp in _trackedMappings)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var info = kvp.Value;
            var mapping = info.Mapping;

            try
            {
                // Check if mapping is already expired
                if (mapping.IsExpired)
                {
                    _logger.LogWarning("Mapping {MappingId} has expired", mapping.Id);
                    expiredMappings.Add(mapping.Id);
                    MappingExpired?.Invoke(this, new MappingExpiredEventArgs(mapping));
                    continue;
                }

                // Check if mapping needs renewal
                if (!mapping.ShouldRenew)
                {
                    continue;
                }

                _logger.LogDebug("Renewing mapping {MappingId}: {Protocol}:{InternalPort}→{ExternalPort}",
                    mapping.Id, mapping.Protocol, mapping.InternalPort, mapping.ExternalPort);

                // Attempt renewal by creating a new mapping with the same parameters
                var renewed = await _natManager.CreateMappingAsync(
                    mapping.Protocol,
                    mapping.InternalPort,
                    mapping.ExternalPort,
                    mapping.Description,
                    cancellationToken);

                if (renewed != null)
                {
                    // Update tracking info
                    info.Mapping = renewed;
                    info.LastRenewal = DateTime.UtcNow;
                    info.RenewalCount++;
                    info.FailureCount = 0; // Reset failure count on success

                    // Update the dictionary entry
                    _trackedMappings[kvp.Key] = info;

                    renewedCount++;
                    _logger.LogInformation("Successfully renewed mapping {MappingId}, new expiration: {ExpiresAt}",
                        mapping.Id, renewed.ExpiresAt);

                    MappingRenewed?.Invoke(this, new MappingRenewedEventArgs(mapping, renewed));
                }
                else
                {
                    info.FailureCount++;
                    _trackedMappings[kvp.Key] = info;

                    _logger.LogWarning("Failed to renew mapping {MappingId} (attempt {FailureCount})",
                        mapping.Id, info.FailureCount);

                    MappingRenewalFailed?.Invoke(this, new MappingRenewalFailedEventArgs(
                        mapping, "Renewal returned null", info.FailureCount));

                    // If too many failures, mark as expired
                    if (info.FailureCount >= _config.MaxRenewalRetries)
                    {
                        _logger.LogError("Mapping {MappingId} exceeded max renewal retries, treating as expired",
                            mapping.Id);
                        expiredMappings.Add(mapping.Id);
                        MappingExpired?.Invoke(this, new MappingExpiredEventArgs(mapping));
                    }
                }
            }
            catch (Exception ex)
            {
                info.FailureCount++;
                _trackedMappings[kvp.Key] = info;

                _logger.LogError(ex, "Error renewing mapping {MappingId}", mapping.Id);

                MappingRenewalFailed?.Invoke(this, new MappingRenewalFailedEventArgs(
                    mapping, ex.Message, info.FailureCount));
            }
        }

        // Remove expired mappings from tracking
        foreach (var expiredId in expiredMappings)
        {
            _trackedMappings.TryRemove(expiredId, out _);
        }

        if (renewedCount > 0 || expiredMappings.Count > 0)
        {
            _logger.LogInformation("Mapping renewal check complete: {Renewed} renewed, {Expired} expired",
                renewedCount, expiredMappings.Count);
        }

        return renewedCount;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _natManager.MappingExpiring -= OnMappingExpiring;
            StopAsync().GetAwaiter().GetResult();
            _cancellationTokenSource?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Internal tracking info for a mapping
/// </summary>
internal class MappingRenewalInfo
{
    public NatMappingResult Mapping { get; set; } = null!;
    public DateTime AddedAt { get; set; }
    public DateTime LastRenewal { get; set; }
    public int RenewalCount { get; set; }
    public int FailureCount { get; set; }
}

/// <summary>
/// Configuration for port mapping renewal service
/// </summary>
public class PortMappingRenewalConfiguration
{
    public int CheckIntervalMinutes { get; set; } = 5;
    public int MaxRenewalRetries { get; set; } = 3;
    public int RenewalBeforeExpirationMinutes { get; set; } = 5;
}

/// <summary>
/// Port mapping renewal service status
/// </summary>
public class PortMappingRenewalStatus
{
    public bool IsRunning { get; set; }
    public int TrackedMappingCount { get; set; }
    public int MappingsNeedingRenewal { get; set; }
    public int ExpiredMappingCount { get; set; }
    public int TotalRenewals { get; set; }
    public int TotalFailures { get; set; }
    public int CheckIntervalMinutes { get; set; }
    public List<MappingRenewalSummary> Mappings { get; set; } = new();
}

/// <summary>
/// Summary of a tracked mapping
/// </summary>
public class MappingRenewalSummary
{
    public string MappingId { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public int InternalPort { get; set; }
    public int ExternalPort { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastRenewal { get; set; }
    public int RenewalCount { get; set; }
    public int FailureCount { get; set; }
    public bool NeedsRenewal { get; set; }
    public bool IsExpired { get; set; }
}

/// <summary>
/// Event args for successful mapping renewal
/// </summary>
public class MappingRenewedEventArgs : EventArgs
{
    public NatMappingResult OldMapping { get; }
    public NatMappingResult NewMapping { get; }

    public MappingRenewedEventArgs(NatMappingResult oldMapping, NatMappingResult newMapping)
    {
        OldMapping = oldMapping;
        NewMapping = newMapping;
    }
}

/// <summary>
/// Event args for failed mapping renewal
/// </summary>
public class MappingRenewalFailedEventArgs : EventArgs
{
    public NatMappingResult Mapping { get; }
    public string Error { get; }
    public int FailureCount { get; }

    public MappingRenewalFailedEventArgs(NatMappingResult mapping, string error, int failureCount)
    {
        Mapping = mapping;
        Error = error;
        FailureCount = failureCount;
    }
}

/// <summary>
/// Event args for expired mapping
/// </summary>
public class MappingExpiredEventArgs : EventArgs
{
    public NatMappingResult Mapping { get; }

    public MappingExpiredEventArgs(NatMappingResult mapping)
    {
        Mapping = mapping;
    }
}
