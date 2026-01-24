using System.Collections.Concurrent;
using System.Net;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Infrastructure.Services.Network.Stun;

/// <summary>
/// STUN service that manages external IP discovery and subscriptions.
/// Compatible with Syncthing's STUN implementation parameters.
/// </summary>
public interface IStunService : IDisposable
{
    /// <summary>
    /// Starts the STUN service.
    /// </summary>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the STUN service.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current external IP address.
    /// </summary>
    Task<IPAddress?> GetExternalIPAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current external endpoint (IP:Port).
    /// </summary>
    Task<IPEndPoint?> GetExternalEndPointAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the detected NAT type.
    /// </summary>
    NatType? NatType { get; }

    /// <summary>
    /// Gets the NAT type description.
    /// </summary>
    string? NatTypeDescription { get; }

    /// <summary>
    /// Gets whether the service is running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Whether hole punching is likely to succeed based on NAT type.
    /// True for Full Cone, Restricted Cone, and Port Restricted Cone NATs.
    /// False for Symmetric NAT and Unknown.
    /// </summary>
    bool IsPunchable { get; }

    /// <summary>
    /// Gets the current external address.
    /// </summary>
    IPEndPoint? ExternalAddress { get; }

    /// <summary>
    /// Performs a fresh NAT type detection.
    /// </summary>
    Task<NatType> DetectNatTypeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current service status.
    /// </summary>
    StunServiceStatus GetStatus();

    /// <summary>
    /// Event raised when external IP changes.
    /// </summary>
    event EventHandler<ExternalAddressChangedEventArgs>? ExternalAddressChanged;

    /// <summary>
    /// Event raised when NAT type changes.
    /// </summary>
    event EventHandler<NatTypeChangedEventArgs>? NatTypeChanged;
}

/// <summary>
/// STUN service status information
/// </summary>
public class StunServiceStatus
{
    public bool IsRunning { get; set; }
    public NatType? NatType { get; set; }
    public string? NatTypeDescription { get; set; }
    public bool IsPunchable { get; set; }
    public IPEndPoint? ExternalEndpoint { get; set; }
    public DateTime LastCheck { get; set; }
    public DateTime LastNatTypeDetection { get; set; }
    public TimeSpan KeepaliveInterval { get; set; }
    public int SuccessfulChecks { get; set; }
    public int FailedChecks { get; set; }
    public List<string> ConfiguredServers { get; set; } = new();
}

/// <summary>
/// Event arguments for external address changes.
/// </summary>
public class ExternalAddressChangedEventArgs : EventArgs
{
    public IPAddress? OldAddress { get; init; }
    public IPAddress? NewAddress { get; init; }
    public IPEndPoint? OldEndPoint { get; init; }
    public IPEndPoint? NewEndPoint { get; init; }
}

/// <summary>
/// STUN service implementation with keepalive and subscription support.
/// </summary>
public class StunService : IStunService
{
    private readonly ILogger<StunService> _logger;
    private readonly SyncConfiguration _config;
    private readonly StunClient _stunClient;
    private readonly Timer _keepAliveTimer;
    private readonly Timer _natTypeDetectionTimer;
    private readonly ConcurrentDictionary<string, StunResult> _serverResults;

    private IPEndPoint? _currentExternalEndPoint;
    private NatType? _natType;
    private string? _natTypeDescription;
    private volatile bool _isRunning;
    private DateTime _lastCheck = DateTime.MinValue;
    private DateTime _lastNatTypeDetection = DateTime.MinValue;
    private int _successfulChecks;
    private int _failedChecks;
    private TimeSpan _currentKeepaliveInterval;

    // Syncthing-compatible intervals
    private static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinKeepAlive = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxKeepAlive = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NatTypeDetectionInterval = TimeSpan.FromMinutes(30);

    // Default STUN servers (Syncthing compatible)
    private static readonly string[] DefaultStunServers =
    [
        "stun.syncthing.net:3478",
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302",
        "stun2.l.google.com:19302",
        "stun.cloudflare.com:3478"
    ];

    public NatType? NatType => _natType;
    public string? NatTypeDescription => _natTypeDescription;
    public bool IsRunning => _isRunning;
    public IPEndPoint? ExternalAddress => _currentExternalEndPoint;

    /// <summary>
    /// Whether hole punching is likely to succeed based on NAT type.
    /// </summary>
    public bool IsPunchable => _natType switch
    {
        Stun.NatType.OpenInternet => true,
        Stun.NatType.FullCone => true,
        Stun.NatType.RestrictedCone => true,
        Stun.NatType.PortRestrictedCone => true,
        Stun.NatType.SymmetricNat => false,
        Stun.NatType.Unknown => false,
        _ => false
    };

    public event EventHandler<ExternalAddressChangedEventArgs>? ExternalAddressChanged;
    public event EventHandler<NatTypeChangedEventArgs>? NatTypeChanged;

    public StunService(ILogger<StunService> logger, ILogger<StunClient> stunClientLogger, IOptions<SyncConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
        _stunClient = new StunClient(stunClientLogger);
        _serverResults = new ConcurrentDictionary<string, StunResult>();
        _keepAliveTimer = new Timer(KeepAliveCallback, null, Timeout.Infinite, Timeout.Infinite);
        _natTypeDetectionTimer = new Timer(NatTypeDetectionCallback, null, Timeout.Infinite, Timeout.Infinite);
        _currentKeepaliveInterval = DefaultKeepAlive;

        _logger.LogInformation("StunService initialized");
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            return true;

        _logger.LogInformation("Starting STUN service");

        try
        {
            // Initial external IP discovery
            var externalEndPoint = await DiscoverExternalEndPointAsync(cancellationToken);

            if (externalEndPoint != null)
            {
                _currentExternalEndPoint = externalEndPoint;
                _successfulChecks++;
                _logger.LogInformation("STUN discovered external address: {ExternalEndPoint}", externalEndPoint);

                // Detect NAT type
                var natResult = await _stunClient.DetectNatTypeAsync(GetStunServers(), DefaultTimeout, cancellationToken);
                _natType = natResult.Type;
                _natTypeDescription = natResult.Description;
                _lastNatTypeDetection = DateTime.UtcNow;
                _logger.LogInformation("STUN detected NAT type: {NatType} - {Description}", natResult.Type, natResult.Description);
            }
            else
            {
                _failedChecks++;
                _logger.LogWarning("STUN could not discover external address - service may have limited functionality");
            }

            // Start keepalive timer with adaptive interval
            _currentKeepaliveInterval = _config.NatTraversal?.StunKeepAliveSeconds != null
                ? TimeSpan.FromSeconds(_config.NatTraversal.StunKeepAliveSeconds.Value)
                : DefaultKeepAlive;

            _keepAliveTimer.Change(_currentKeepaliveInterval, _currentKeepaliveInterval);

            // Start periodic NAT type detection (less frequent)
            _natTypeDetectionTimer.Change(NatTypeDetectionInterval, NatTypeDetectionInterval);

            _isRunning = true;
            _lastCheck = DateTime.UtcNow;

            _logger.LogInformation("STUN service started with keepalive interval {Interval}, IsPunchable: {IsPunchable}",
                _currentKeepaliveInterval, IsPunchable);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start STUN service");
            return false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping STUN service");

        _keepAliveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _natTypeDetectionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _isRunning = false;
        _serverResults.Clear();

        _logger.LogInformation("STUN service stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Performs a fresh NAT type detection.
    /// </summary>
    public async Task<NatType> DetectNatTypeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var natResult = await _stunClient.DetectNatTypeAsync(GetStunServers(), DefaultTimeout, cancellationToken);

            var oldType = _natType;
            _natType = natResult.Type;
            _natTypeDescription = natResult.Description;
            _lastNatTypeDetection = DateTime.UtcNow;

            // Notify if NAT type changed
            if (oldType.HasValue && oldType != _natType)
            {
                _logger.LogInformation("NAT type changed: {OldType} -> {NewType} ({Description})",
                    oldType, _natType, _natTypeDescription);

                NatTypeChanged?.Invoke(this, new NatTypeChangedEventArgs(
                    oldType.Value,
                    _natType.Value,
                    _natTypeDescription ?? string.Empty));
            }

            return _natType ?? Stun.NatType.Unknown;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAT type detection failed");
            return Stun.NatType.Unknown;
        }
    }

    /// <summary>
    /// Gets the current service status.
    /// </summary>
    public StunServiceStatus GetStatus()
    {
        return new StunServiceStatus
        {
            IsRunning = _isRunning,
            NatType = _natType,
            NatTypeDescription = _natTypeDescription,
            IsPunchable = IsPunchable,
            ExternalEndpoint = _currentExternalEndPoint,
            LastCheck = _lastCheck,
            LastNatTypeDetection = _lastNatTypeDetection,
            KeepaliveInterval = _currentKeepaliveInterval,
            SuccessfulChecks = _successfulChecks,
            FailedChecks = _failedChecks,
            ConfiguredServers = GetStunServers().ToList()
        };
    }

    public async Task<IPAddress?> GetExternalIPAsync(CancellationToken cancellationToken = default)
    {
        // Return cached value if recent
        if (_currentExternalEndPoint != null && DateTime.UtcNow - _lastCheck < CheckInterval)
        {
            return _currentExternalEndPoint.Address;
        }

        // Refresh
        var endPoint = await DiscoverExternalEndPointAsync(cancellationToken);
        return endPoint?.Address;
    }

    public async Task<IPEndPoint?> GetExternalEndPointAsync(CancellationToken cancellationToken = default)
    {
        // Return cached value if recent
        if (_currentExternalEndPoint != null && DateTime.UtcNow - _lastCheck < CheckInterval)
        {
            return _currentExternalEndPoint;
        }

        // Refresh
        return await DiscoverExternalEndPointAsync(cancellationToken);
    }

    private async Task<IPEndPoint?> DiscoverExternalEndPointAsync(CancellationToken cancellationToken = default)
    {
        var servers = GetStunServers();

        foreach (var server in servers)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var parts = server.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 3478;

            try
            {
                var result = await _stunClient.BindingRequestAsync(host, port, DefaultTimeout, cancellationToken);

                if (result?.MappedEndPoint != null)
                {
                    _serverResults[server] = result;
                    _lastCheck = DateTime.UtcNow;
                    return result.MappedEndPoint;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "STUN request to {Server} failed", server);
            }
        }

        return null;
    }

    private void KeepAliveCallback(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var oldEndPoint = _currentExternalEndPoint;
                var newEndPoint = await DiscoverExternalEndPointAsync();

                if (newEndPoint != null)
                {
                    _successfulChecks++;
                    _lastCheck = DateTime.UtcNow;

                    var changed = oldEndPoint == null ||
                                  !oldEndPoint.Address.Equals(newEndPoint.Address) ||
                                  oldEndPoint.Port != newEndPoint.Port;

                    if (changed)
                    {
                        _currentExternalEndPoint = newEndPoint;
                        _logger.LogInformation("STUN external address changed: {OldEndPoint} -> {NewEndPoint}", oldEndPoint, newEndPoint);

                        ExternalAddressChanged?.Invoke(this, new ExternalAddressChangedEventArgs
                        {
                            OldAddress = oldEndPoint?.Address,
                            NewAddress = newEndPoint.Address,
                            OldEndPoint = oldEndPoint,
                            NewEndPoint = newEndPoint
                        });

                        // If address changed, trigger NAT type re-detection
                        _ = DetectNatTypeAsync();
                    }

                    // Adaptive keepalive: increase interval on success (up to max)
                    AdjustKeepaliveInterval(success: true);
                }
                else
                {
                    _failedChecks++;
                    // Adaptive keepalive: decrease interval on failure (down to min)
                    AdjustKeepaliveInterval(success: false);
                }
            }
            catch (Exception ex)
            {
                _failedChecks++;
                _logger.LogWarning(ex, "STUN keepalive check failed");
                AdjustKeepaliveInterval(success: false);
            }
        });
    }

    private void NatTypeDetectionCallback(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DetectNatTypeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Periodic NAT type detection failed");
            }
        });
    }

    /// <summary>
    /// Adjusts the keepalive interval based on success/failure.
    /// Based on Syncthing's adaptive STUN keepalive (20-180 seconds).
    /// </summary>
    private void AdjustKeepaliveInterval(bool success)
    {
        var oldInterval = _currentKeepaliveInterval;

        if (success)
        {
            // Increase interval by 10% on success, up to max
            var newInterval = TimeSpan.FromSeconds(_currentKeepaliveInterval.TotalSeconds * 1.1);
            _currentKeepaliveInterval = newInterval > MaxKeepAlive ? MaxKeepAlive : newInterval;
        }
        else
        {
            // Decrease interval by 50% on failure, down to min
            var newInterval = TimeSpan.FromSeconds(_currentKeepaliveInterval.TotalSeconds * 0.5);
            _currentKeepaliveInterval = newInterval < MinKeepAlive ? MinKeepAlive : newInterval;
        }

        // Only update timer if interval changed significantly (>5%)
        var change = Math.Abs(_currentKeepaliveInterval.TotalSeconds - oldInterval.TotalSeconds) / oldInterval.TotalSeconds;
        if (change > 0.05)
        {
            _keepAliveTimer.Change(_currentKeepaliveInterval, _currentKeepaliveInterval);
            _logger.LogDebug("STUN keepalive interval adjusted: {OldInterval}s -> {NewInterval}s",
                (int)oldInterval.TotalSeconds, (int)_currentKeepaliveInterval.TotalSeconds);
        }
    }

    private string[] GetStunServers()
    {
        // Use configured servers or defaults
        if (_config.NatTraversal?.StunServers != null && _config.NatTraversal.StunServers.Count > 0)
        {
            return _config.NatTraversal.StunServers.ToArray();
        }

        return DefaultStunServers;
    }

    public void Dispose()
    {
        if (_isRunning)
        {
            _ = StopAsync();
        }

        _keepAliveTimer.Dispose();
        _natTypeDetectionTimer.Dispose();
        _stunClient.Dispose();
    }
}
