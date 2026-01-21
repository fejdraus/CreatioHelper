using System.Collections.Concurrent;
using System.Net;
using CreatioHelper.Infrastructure.Services.Network.Stun;
using CreatioHelper.Infrastructure.Services.Network.UPnP;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Nat;

/// <summary>
/// NAT Mapping Lifecycle Service that manages port mappings across all NAT traversal methods.
/// Handles automatic renewal, fallback between methods, and mapping persistence.
/// Based on Syncthing's lib/nat/service.go
/// </summary>
public interface INatMappingService : IHostedService
{
    /// <summary>
    /// Request a new port mapping using the best available method.
    /// </summary>
    Task<NatMapping?> RequestMappingAsync(ushort internalPort, string protocol, string description = "CreatioHelper", CancellationToken ct = default);

    /// <summary>
    /// Release an existing port mapping.
    /// </summary>
    Task ReleaseMappingAsync(NatMapping mapping);

    /// <summary>
    /// Get all active mappings.
    /// </summary>
    IReadOnlyList<NatMapping> ActiveMappings { get; }

    /// <summary>
    /// Get the current external address if known.
    /// </summary>
    IPAddress? ExternalAddress { get; }

    /// <summary>
    /// Get the detected NAT type.
    /// </summary>
    NatType? DetectedNatType { get; }

    /// <summary>
    /// Whether the service is running and has at least one method available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Event raised when a mapping is created, renewed, or expires.
    /// </summary>
    event Action<NatMapping, NatMappingEventType>? OnMappingChanged;

    /// <summary>
    /// Event raised when external address changes.
    /// </summary>
    event Action<IPAddress?>? OnExternalAddressChanged;
}

/// <summary>
/// NAT mapping event types
/// </summary>
public enum NatMappingEventType
{
    Created,
    Renewed,
    Expired,
    Released,
    Failed
}

/// <summary>
/// Represents a NAT port mapping
/// </summary>
public record NatMapping
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ushort InternalPort { get; init; }
    public ushort ExternalPort { get; init; }
    public string Protocol { get; init; } = "TCP";
    public NatMappingMethod Method { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }
    public IPAddress? ExternalAddress { get; init; }
    public string Description { get; init; } = string.Empty;
    public int RenewalCount { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public TimeSpan TimeToExpire => ExpiresAt - DateTime.UtcNow;
    public bool ShouldRenew => !IsExpired && TimeToExpire < TimeSpan.FromMinutes(15);
}

/// <summary>
/// NAT mapping method
/// </summary>
public enum NatMappingMethod
{
    UPnP,
    NatPmp,
    Stun,
    Manual
}

/// <summary>
/// Implementation of NAT Mapping Lifecycle Service
/// </summary>
public class NatMappingService : BackgroundService, INatMappingService
{
    private readonly ILogger<NatMappingService> _logger;
    private readonly IUPnPService? _upnpService;
    private readonly NatPmpClient? _natPmpClient;
    private readonly IStunService? _stunService;
    private readonly IgdServiceClient? _igdClient;
    private readonly NatMappingServiceOptions _options;

    private readonly ConcurrentDictionary<string, NatMapping> _mappings = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IPAddress? _externalAddress;
    private NatType? _detectedNatType;
    private bool _upnpAvailable;
    private bool _natPmpAvailable;
    private bool _stunPunchable;

    public IReadOnlyList<NatMapping> ActiveMappings => _mappings.Values.Where(m => !m.IsExpired).ToList();
    public IPAddress? ExternalAddress => _externalAddress;
    public NatType? DetectedNatType => _detectedNatType;
    public bool IsAvailable => _upnpAvailable || _natPmpAvailable || _stunPunchable;

    public event Action<NatMapping, NatMappingEventType>? OnMappingChanged;
    public event Action<IPAddress?>? OnExternalAddressChanged;

    public NatMappingService(
        ILogger<NatMappingService> logger,
        NatMappingServiceOptions options,
        IUPnPService? upnpService = null,
        NatPmpClient? natPmpClient = null,
        IStunService? stunService = null,
        IgdServiceClient? igdClient = null)
    {
        _logger = logger;
        _options = options;
        _upnpService = upnpService;
        _natPmpClient = natPmpClient;
        _stunService = stunService;
        _igdClient = igdClient;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting NAT Mapping Service");

        // Discover available methods
        await DiscoverMethodsAsync(cancellationToken);

        await base.StartAsync(cancellationToken);

        _logger.LogInformation("NAT Mapping Service started. Available methods: UPnP={UPnP}, NAT-PMP={NatPmp}, STUN={Stun}",
            _upnpAvailable, _natPmpAvailable, _stunPunchable);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var renewalInterval = TimeSpan.FromMinutes(_options.RenewalCheckIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(renewalInterval, stoppingToken);

                // Check and renew expiring mappings
                await RenewExpiringMappingsAsync(stoppingToken);

                // Clean up expired mappings
                CleanupExpiredMappings();

                // Periodically refresh external address
                await RefreshExternalAddressAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in NAT mapping renewal loop");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping NAT Mapping Service");

        // Release all active mappings
        foreach (var mapping in _mappings.Values.ToList())
        {
            try
            {
                await ReleaseMappingInternalAsync(mapping, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release mapping {MappingId} during shutdown", mapping.Id);
            }
        }

        _mappings.Clear();
        await base.StopAsync(cancellationToken);

        _logger.LogInformation("NAT Mapping Service stopped");
    }

    public async Task<NatMapping?> RequestMappingAsync(ushort internalPort, string protocol, string description = "CreatioHelper", CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _logger.LogDebug("Requesting mapping for {Protocol}:{Port}", protocol, internalPort);

            NatMapping? mapping = null;

            // Priority 1: Try UPnP
            if (_upnpAvailable && _upnpService != null)
            {
                mapping = await TryCreateUpnpMappingAsync(internalPort, protocol, description, ct);
            }

            // Priority 2: Try NAT-PMP
            if (mapping == null && _natPmpAvailable && _natPmpClient != null)
            {
                mapping = await TryCreateNatPmpMappingAsync(internalPort, protocol, description, ct);
            }

            // Priority 3: Use STUN (hole punching - just record the external address/port)
            if (mapping == null && _stunPunchable && _stunService != null)
            {
                mapping = await TryCreateStunMappingAsync(internalPort, protocol, description, ct);
            }

            if (mapping != null)
            {
                _mappings[mapping.Id] = mapping;
                OnMappingChanged?.Invoke(mapping, NatMappingEventType.Created);

                _logger.LogInformation("Created mapping {Id}: {Protocol} {InternalPort}→{ExternalPort} via {Method}",
                    mapping.Id, protocol, internalPort, mapping.ExternalPort, mapping.Method);
            }
            else
            {
                _logger.LogWarning("Failed to create mapping for {Protocol}:{Port}", protocol, internalPort);
            }

            return mapping;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ReleaseMappingAsync(NatMapping mapping)
    {
        await _lock.WaitAsync();
        try
        {
            await ReleaseMappingInternalAsync(mapping, CancellationToken.None);
            _mappings.TryRemove(mapping.Id, out _);
            OnMappingChanged?.Invoke(mapping, NatMappingEventType.Released);

            _logger.LogInformation("Released mapping {Id}", mapping.Id);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DiscoverMethodsAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();

        // Check UPnP
        if (_upnpService != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    _upnpAvailable = await _upnpService.IsAvailableAsync(ct);
                    if (_upnpAvailable)
                    {
                        var externalIp = await _upnpService.GetExternalIPAddressAsync(ct);
                        if (!string.IsNullOrEmpty(externalIp) && IPAddress.TryParse(externalIp, out var addr))
                        {
                            UpdateExternalAddress(addr);
                        }
                    }
                    _logger.LogDebug("UPnP availability: {Available}", _upnpAvailable);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to check UPnP availability");
                    _upnpAvailable = false;
                }
            }, ct));
        }

        // Check NAT-PMP
        if (_natPmpClient != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    _natPmpAvailable = await _natPmpClient.DiscoverGatewayAsync(ct);
                    if (_natPmpAvailable)
                    {
                        var addr = await _natPmpClient.GetExternalAddressAsync(ct);
                        if (addr != null)
                        {
                            UpdateExternalAddress(addr);
                        }
                    }
                    _logger.LogDebug("NAT-PMP availability: {Available}", _natPmpAvailable);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to check NAT-PMP availability");
                    _natPmpAvailable = false;
                }
            }, ct));
        }

        // Check STUN and detect NAT type
        if (_stunService != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await _stunService.StartAsync(ct);
                    _detectedNatType = _stunService.NatType;

                    // STUN hole punching works for Full Cone, Restricted Cone, and Port Restricted
                    _stunPunchable = _detectedNatType != null &&
                                    _detectedNatType != NatType.Unknown &&
                                    _detectedNatType != NatType.SymmetricNat;

                    var externalEndpoint = await _stunService.GetExternalEndPointAsync(ct);
                    if (externalEndpoint != null)
                    {
                        UpdateExternalAddress(externalEndpoint.Address);
                    }

                    _logger.LogDebug("STUN NAT type: {NatType}, Punchable: {Punchable}", _detectedNatType, _stunPunchable);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to check STUN/NAT type");
                    _stunPunchable = false;
                }
            }, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task<NatMapping?> TryCreateUpnpMappingAsync(ushort internalPort, string protocol, string description, CancellationToken ct)
    {
        try
        {
            var success = await _upnpService!.AddPortMappingAsync(
                internalPort, internalPort, protocol, description, _options.DefaultLeaseDurationSeconds, ct);

            if (success)
            {
                var externalIpStr = await _upnpService.GetExternalIPAddressAsync(ct);
                IPAddress.TryParse(externalIpStr, out var externalIp);

                return new NatMapping
                {
                    InternalPort = internalPort,
                    ExternalPort = internalPort,
                    Protocol = protocol,
                    Method = NatMappingMethod.UPnP,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(_options.DefaultLeaseDurationSeconds > 0 ? _options.DefaultLeaseDurationSeconds : 86400),
                    ExternalAddress = externalIp ?? _externalAddress,
                    Description = description
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create UPnP mapping");
        }

        return null;
    }

    private async Task<NatMapping?> TryCreateNatPmpMappingAsync(ushort internalPort, string protocol, string description, CancellationToken ct)
    {
        try
        {
            var pmpMapping = await _natPmpClient!.CreateMappingAsync(
                protocol, internalPort, internalPort, _options.DefaultLeaseDurationSeconds, ct);

            if (pmpMapping != null)
            {
                var externalIp = await _natPmpClient.GetExternalAddressAsync(ct);

                return new NatMapping
                {
                    InternalPort = (ushort)pmpMapping.InternalPort,
                    ExternalPort = (ushort)pmpMapping.ExternalPort,
                    Protocol = protocol,
                    Method = NatMappingMethod.NatPmp,
                    ExpiresAt = pmpMapping.ExpiresAt,
                    ExternalAddress = externalIp ?? _externalAddress,
                    Description = description
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create NAT-PMP mapping");
        }

        return null;
    }

    private async Task<NatMapping?> TryCreateStunMappingAsync(ushort internalPort, string protocol, string description, CancellationToken ct)
    {
        try
        {
            var externalEndpoint = await _stunService!.GetExternalEndPointAsync(ct);

            if (externalEndpoint != null)
            {
                // STUN doesn't create real mappings, but we record the discovered external address
                // The actual hole punching happens at the application layer
                return new NatMapping
                {
                    InternalPort = internalPort,
                    ExternalPort = (ushort)externalEndpoint.Port,
                    Protocol = protocol,
                    Method = NatMappingMethod.Stun,
                    ExpiresAt = DateTime.MaxValue, // STUN mappings don't expire (but may change)
                    ExternalAddress = externalEndpoint.Address,
                    Description = description
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get STUN mapping");
        }

        return null;
    }

    private async Task ReleaseMappingInternalAsync(NatMapping mapping, CancellationToken ct)
    {
        try
        {
            switch (mapping.Method)
            {
                case NatMappingMethod.UPnP when _upnpService != null:
                    await _upnpService.DeletePortMappingAsync(mapping.ExternalPort, mapping.Protocol, ct);
                    break;

                case NatMappingMethod.NatPmp when _natPmpClient != null:
                    await _natPmpClient.DeleteMappingAsync(mapping.Protocol, mapping.InternalPort, ct);
                    break;

                case NatMappingMethod.Stun:
                    // STUN mappings don't need explicit deletion
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to release mapping {Id}", mapping.Id);
        }
    }

    private async Task RenewExpiringMappingsAsync(CancellationToken ct)
    {
        var toRenew = _mappings.Values.Where(m => m.ShouldRenew && !m.IsExpired).ToList();

        foreach (var mapping in toRenew)
        {
            try
            {
                _logger.LogDebug("Renewing mapping {Id} (expires in {TimeToExpire})", mapping.Id, mapping.TimeToExpire);

                NatMapping? renewed = null;

                switch (mapping.Method)
                {
                    case NatMappingMethod.UPnP when _upnpService != null:
                        var upnpSuccess = await _upnpService.AddPortMappingAsync(
                            mapping.ExternalPort, mapping.InternalPort, mapping.Protocol,
                            mapping.Description, _options.DefaultLeaseDurationSeconds, ct);
                        if (upnpSuccess)
                        {
                            renewed = mapping with
                            {
                                ExpiresAt = DateTime.UtcNow.AddSeconds(_options.DefaultLeaseDurationSeconds > 0 ? _options.DefaultLeaseDurationSeconds : 86400),
                                RenewalCount = mapping.RenewalCount + 1
                            };
                        }
                        break;

                    case NatMappingMethod.NatPmp when _natPmpClient != null:
                        var pmpResult = await _natPmpClient.CreateMappingAsync(
                            mapping.Protocol, mapping.InternalPort, mapping.ExternalPort,
                            _options.DefaultLeaseDurationSeconds, ct);
                        if (pmpResult != null)
                        {
                            renewed = mapping with
                            {
                                ExpiresAt = pmpResult.ExpiresAt,
                                RenewalCount = mapping.RenewalCount + 1
                            };
                        }
                        break;

                    case NatMappingMethod.Stun:
                        // STUN mappings don't need renewal, but we can refresh the external address
                        if (_stunService != null)
                        {
                            var endpoint = await _stunService.GetExternalEndPointAsync(ct);
                            if (endpoint != null)
                            {
                                renewed = mapping with
                                {
                                    ExternalPort = (ushort)endpoint.Port,
                                    ExternalAddress = endpoint.Address
                                };
                            }
                        }
                        break;
                }

                if (renewed != null)
                {
                    _mappings[mapping.Id] = renewed;
                    OnMappingChanged?.Invoke(renewed, NatMappingEventType.Renewed);
                    _logger.LogDebug("Renewed mapping {Id}, new expiry: {ExpiresAt}", renewed.Id, renewed.ExpiresAt);
                }
                else
                {
                    _logger.LogWarning("Failed to renew mapping {Id}", mapping.Id);
                    OnMappingChanged?.Invoke(mapping, NatMappingEventType.Failed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error renewing mapping {Id}", mapping.Id);
            }
        }
    }

    private void CleanupExpiredMappings()
    {
        var expired = _mappings.Values.Where(m => m.IsExpired).ToList();

        foreach (var mapping in expired)
        {
            if (_mappings.TryRemove(mapping.Id, out _))
            {
                OnMappingChanged?.Invoke(mapping, NatMappingEventType.Expired);
                _logger.LogDebug("Removed expired mapping {Id}", mapping.Id);
            }
        }
    }

    private async Task RefreshExternalAddressAsync(CancellationToken ct)
    {
        IPAddress? newAddress = null;

        try
        {
            if (_upnpService != null && _upnpAvailable)
            {
                var ipStr = await _upnpService.GetExternalIPAddressAsync(ct);
                if (!string.IsNullOrEmpty(ipStr) && IPAddress.TryParse(ipStr, out var addr))
                {
                    newAddress = addr;
                }
            }
            else if (_natPmpClient != null && _natPmpAvailable)
            {
                newAddress = await _natPmpClient.GetExternalAddressAsync(ct);
            }
            else if (_stunService != null)
            {
                var endpoint = await _stunService.GetExternalEndPointAsync(ct);
                newAddress = endpoint?.Address;
            }

            if (newAddress != null)
            {
                UpdateExternalAddress(newAddress);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh external address");
        }
    }

    private void UpdateExternalAddress(IPAddress newAddress)
    {
        if (_externalAddress == null || !_externalAddress.Equals(newAddress))
        {
            var oldAddress = _externalAddress;
            _externalAddress = newAddress;

            if (oldAddress != null)
            {
                _logger.LogInformation("External address changed: {OldAddress} → {NewAddress}", oldAddress, newAddress);
            }
            else
            {
                _logger.LogInformation("External address discovered: {Address}", newAddress);
            }

            OnExternalAddressChanged?.Invoke(newAddress);
        }
    }
}

/// <summary>
/// Options for NatMappingService
/// </summary>
public class NatMappingServiceOptions
{
    /// <summary>
    /// Default lease duration in seconds for new mappings.
    /// 0 = indefinite (where supported), otherwise gateway default.
    /// </summary>
    public int DefaultLeaseDurationSeconds { get; set; } = 3600;

    /// <summary>
    /// How often to check for expiring mappings (in minutes).
    /// </summary>
    public int RenewalCheckIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Enable UPnP port mapping.
    /// </summary>
    public bool EnableUPnP { get; set; } = true;

    /// <summary>
    /// Enable NAT-PMP port mapping.
    /// </summary>
    public bool EnableNatPmp { get; set; } = true;

    /// <summary>
    /// Enable STUN for NAT type detection.
    /// </summary>
    public bool EnableStun { get; set; } = true;
}
