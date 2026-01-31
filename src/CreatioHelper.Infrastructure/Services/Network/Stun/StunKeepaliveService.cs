using System.Net;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Stun;

/// <summary>
/// STUN keepalive service for maintaining NAT bindings.
/// Periodically sends STUN binding requests to keep NAT mappings alive
/// and detect external address changes.
/// Based on Syncthing's STUN keepalive mechanism.
/// </summary>
public class StunKeepaliveService : IDisposable
{
    private readonly ILogger<StunKeepaliveService> _logger;
    private readonly StunClient _stunClient;
    private readonly StunKeepaliveConfiguration _config;

    private Timer? _keepaliveTimer;
    private CancellationTokenSource? _cancellationTokenSource;
    private IPEndPoint? _lastKnownExternalEndpoint;
    private NatType? _lastDetectedNatType;
    private DateTime _lastSuccessfulCheck = DateTime.MinValue;
    private int _consecutiveFailures;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Current external endpoint as detected by STUN
    /// </summary>
    public IPEndPoint? ExternalEndpoint => _lastKnownExternalEndpoint;

    /// <summary>
    /// Last detected NAT type
    /// </summary>
    public NatType? DetectedNatType => _lastDetectedNatType;

    /// <summary>
    /// Is the service running
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Event raised when external address changes
    /// </summary>
    public event EventHandler<StunAddressChangedEventArgs>? ExternalAddressChanged;

    /// <summary>
    /// Event raised when NAT type changes
    /// </summary>
    public event EventHandler<NatTypeChangedEventArgs>? NatTypeChanged;

    /// <summary>
    /// Event raised after each keepalive check
    /// </summary>
    public event EventHandler<StunKeepaliveResultEventArgs>? KeepaliveCompleted;

    public StunKeepaliveService(
        ILogger<StunKeepaliveService> logger,
        StunClient stunClient,
        StunKeepaliveConfiguration config)
    {
        _logger = logger;
        _stunClient = stunClient;
        _config = config;
    }

    /// <summary>
    /// Start the keepalive service
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            return;

        if (!_config.Enabled || !_config.Servers.Any())
        {
            _logger.LogInformation("STUN keepalive service disabled or no servers configured");
            return;
        }

        _logger.LogInformation("Starting STUN keepalive service with {ServerCount} servers, interval {Interval}s",
            _config.Servers.Count, _config.KeepaliveIntervalSeconds);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;

        // Perform initial check immediately
        await PerformKeepaliveCheckAsync(_cancellationTokenSource.Token);

        // Start periodic keepalive timer
        _keepaliveTimer = new Timer(
            async _ => await PerformKeepaliveCheckAsync(_cancellationTokenSource.Token),
            null,
            TimeSpan.FromSeconds(_config.KeepaliveIntervalSeconds),
            TimeSpan.FromSeconds(_config.KeepaliveIntervalSeconds));

        _logger.LogInformation("STUN keepalive service started");
    }

    /// <summary>
    /// Stop the keepalive service
    /// </summary>
    public Task StopAsync()
    {
        if (!_isRunning)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping STUN keepalive service");

        _cancellationTokenSource?.Cancel();
        _keepaliveTimer?.Dispose();
        _keepaliveTimer = null;
        _isRunning = false;

        _logger.LogInformation("STUN keepalive service stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Force an immediate keepalive check
    /// </summary>
    public async Task<StunKeepaliveResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        return await PerformKeepaliveCheckAsync(cancellationToken);
    }

    /// <summary>
    /// Get current service status
    /// </summary>
    public StunKeepaliveStatus GetStatus()
    {
        return new StunKeepaliveStatus
        {
            IsRunning = _isRunning,
            Enabled = _config.Enabled,
            ExternalEndpoint = _lastKnownExternalEndpoint,
            DetectedNatType = _lastDetectedNatType,
            LastSuccessfulCheck = _lastSuccessfulCheck,
            ConsecutiveFailures = _consecutiveFailures,
            KeepaliveIntervalSeconds = _config.KeepaliveIntervalSeconds,
            Servers = _config.Servers.ToList()
        };
    }

    private async Task<StunKeepaliveResult> PerformKeepaliveCheckAsync(CancellationToken cancellationToken)
    {
        var result = new StunKeepaliveResult
        {
            CheckTime = DateTime.UtcNow
        };

        try
        {
            _logger.LogDebug("Performing STUN keepalive check");

            StunResult? stunResult = null;

            // Try servers in order until one succeeds
            foreach (var server in _config.Servers)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var parts = server.Split(':');
                    var host = parts[0];
                    var port = parts.Length > 1 ? int.Parse(parts[1]) : 3478;

                    stunResult = await _stunClient.BindingRequestAsync(
                        host,
                        port,
                        TimeSpan.FromSeconds(_config.RequestTimeoutSeconds),
                        cancellationToken);

                    if (stunResult?.MappedEndPoint != null)
                    {
                        result.Server = server;
                        result.ExternalEndpoint = stunResult.MappedEndPoint;
                        result.LocalEndpoint = stunResult.LocalEndPoint;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "STUN request to {Server} failed", server);
                }
            }

            if (result.ExternalEndpoint != null)
            {
                result.Success = true;
                _lastSuccessfulCheck = result.CheckTime;
                _consecutiveFailures = 0;

                // Check if external address changed
                var oldEndpoint = _lastKnownExternalEndpoint;
                _lastKnownExternalEndpoint = result.ExternalEndpoint;

                if (oldEndpoint == null || !EndpointsEqual(oldEndpoint, result.ExternalEndpoint))
                {
                    result.AddressChanged = true;
                    _logger.LogInformation("External address changed: {OldEndpoint} → {NewEndpoint}",
                        oldEndpoint, result.ExternalEndpoint);

                    ExternalAddressChanged?.Invoke(this, new StunAddressChangedEventArgs(
                        oldEndpoint,
                        result.ExternalEndpoint));
                }

                _logger.LogDebug("STUN keepalive successful: {ExternalEndpoint} via {Server}",
                    result.ExternalEndpoint, result.Server);
            }
            else
            {
                result.Success = false;
                _consecutiveFailures++;
                _logger.LogWarning("STUN keepalive failed - no servers responded (failures: {FailureCount})",
                    _consecutiveFailures);
            }

            // Periodically refresh NAT type detection
            if (result.Success && ShouldRefreshNatType())
            {
                await RefreshNatTypeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Operation cancelled";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _consecutiveFailures++;
            _logger.LogWarning(ex, "STUN keepalive check failed");
        }

        result.Duration = DateTime.UtcNow - result.CheckTime;

        // Raise completion event
        KeepaliveCompleted?.Invoke(this, new StunKeepaliveResultEventArgs(result));

        return result;
    }

    private async Task RefreshNatTypeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var natTypeResult = await _stunClient.DetectNatTypeAsync(
                _config.Servers.ToArray(),
                TimeSpan.FromSeconds(_config.RequestTimeoutSeconds),
                cancellationToken);

            var oldNatType = _lastDetectedNatType;
            _lastDetectedNatType = natTypeResult.Type;

            if (oldNatType != natTypeResult.Type)
            {
                _logger.LogInformation("NAT type changed: {OldType} → {NewType}",
                    oldNatType, natTypeResult.Type);

                NatTypeChanged?.Invoke(this, new NatTypeChangedEventArgs(
                    oldNatType,
                    natTypeResult.Type,
                    natTypeResult.Description));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh NAT type");
        }
    }

    private bool ShouldRefreshNatType()
    {
        // Refresh NAT type every 5 minutes or if not yet detected
        return _lastDetectedNatType == null ||
               (DateTime.UtcNow - _lastSuccessfulCheck).TotalMinutes > 5;
    }

    private static bool EndpointsEqual(IPEndPoint a, IPEndPoint b)
    {
        return a.Address.Equals(b.Address) && a.Port == b.Port;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopAsync().GetAwaiter().GetResult();
            _cancellationTokenSource?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Configuration for STUN keepalive service
/// </summary>
public class StunKeepaliveConfiguration
{
    public bool Enabled { get; set; } = true;

    public List<string> Servers { get; set; } = new();

    public int KeepaliveIntervalSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Result of a STUN keepalive check
/// </summary>
public class StunKeepaliveResult
{
    public bool Success { get; set; }
    public string? Server { get; set; }
    public IPEndPoint? ExternalEndpoint { get; set; }
    public IPEndPoint? LocalEndpoint { get; set; }
    public bool AddressChanged { get; set; }
    public DateTime CheckTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// STUN keepalive service status
/// </summary>
public class StunKeepaliveStatus
{
    public bool IsRunning { get; set; }
    public bool Enabled { get; set; }
    public IPEndPoint? ExternalEndpoint { get; set; }
    public NatType? DetectedNatType { get; set; }
    public DateTime LastSuccessfulCheck { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int KeepaliveIntervalSeconds { get; set; }
    public List<string> Servers { get; set; } = new();
}

/// <summary>
/// Event args for STUN address change
/// </summary>
public class StunAddressChangedEventArgs : EventArgs
{
    public IPEndPoint? OldEndpoint { get; }
    public IPEndPoint? NewEndpoint { get; }

    public StunAddressChangedEventArgs(IPEndPoint? oldEndpoint, IPEndPoint? newEndpoint)
    {
        OldEndpoint = oldEndpoint;
        NewEndpoint = newEndpoint;
    }
}

/// <summary>
/// Event args for NAT type change
/// </summary>
public class NatTypeChangedEventArgs : EventArgs
{
    public NatType? OldType { get; }
    public NatType? NewType { get; }
    public string Description { get; }

    public NatTypeChangedEventArgs(NatType? oldType, NatType? newType, string description)
    {
        OldType = oldType;
        NewType = newType;
        Description = description;
    }
}

/// <summary>
/// Event args for keepalive completion
/// </summary>
public class StunKeepaliveResultEventArgs : EventArgs
{
    public StunKeepaliveResult Result { get; }

    public StunKeepaliveResultEventArgs(StunKeepaliveResult result)
    {
        Result = result;
    }
}
