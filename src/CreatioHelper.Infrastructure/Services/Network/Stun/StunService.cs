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
    /// Gets whether the service is running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Event raised when external IP changes.
    /// </summary>
    event EventHandler<ExternalAddressChangedEventArgs>? ExternalAddressChanged;
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
    private readonly ConcurrentDictionary<string, StunResult> _serverResults;

    private IPEndPoint? _currentExternalEndPoint;
    private NatType? _natType;
    private volatile bool _isRunning;
    private DateTime _lastCheck = DateTime.MinValue;

    // Syncthing-compatible intervals
    private static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

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
    public bool IsRunning => _isRunning;

    public event EventHandler<ExternalAddressChangedEventArgs>? ExternalAddressChanged;

    public StunService(ILogger<StunService> logger, ILogger<StunClient> stunClientLogger, IOptions<SyncConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
        _stunClient = new StunClient(stunClientLogger);
        _serverResults = new ConcurrentDictionary<string, StunResult>();
        _keepAliveTimer = new Timer(KeepAliveCallback, null, Timeout.Infinite, Timeout.Infinite);

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
                _logger.LogInformation("STUN discovered external address: {ExternalEndPoint}", externalEndPoint);

                // Detect NAT type
                var natResult = await _stunClient.DetectNatTypeAsync(GetStunServers(), DefaultTimeout, cancellationToken);
                _natType = natResult.Type;
                _logger.LogInformation("STUN detected NAT type: {NatType} - {Description}", natResult.Type, natResult.Description);
            }
            else
            {
                _logger.LogWarning("STUN could not discover external address - service may have limited functionality");
            }

            // Start keepalive timer
            var keepAliveInterval = _config.NatTraversal?.StunKeepAliveSeconds != null
                ? TimeSpan.FromSeconds(_config.NatTraversal.StunKeepAliveSeconds.Value)
                : DefaultKeepAlive;

            _keepAliveTimer.Change(keepAliveInterval, keepAliveInterval);

            _isRunning = true;
            _lastCheck = DateTime.UtcNow;

            _logger.LogInformation("STUN service started with keepalive interval {Interval}", keepAliveInterval);
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
        _isRunning = false;
        _serverResults.Clear();

        _logger.LogInformation("STUN service stopped");
        return Task.CompletedTask;
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
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "STUN keepalive check failed");
            }
        });
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
        _stunClient.Dispose();
    }
}
