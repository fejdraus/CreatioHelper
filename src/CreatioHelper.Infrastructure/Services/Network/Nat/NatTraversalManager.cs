using System.Collections.Concurrent;
using System.Net;
using CreatioHelper.Infrastructure.Services.Network.Stun;
using CreatioHelper.Infrastructure.Services.Network.UPnP;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Nat;

/// <summary>
/// Unified NAT traversal manager that coordinates UPnP, NAT-PMP, and STUN.
/// Based on Syncthing's NAT service from lib/nat/service.go
/// </summary>
public class NatTraversalManager : INatTraversalManager
{
    private readonly ILogger<NatTraversalManager> _logger;
    private readonly IUPnPService? _upnpService;
    private readonly NatPmpClient? _natPmpClient;
    private readonly StunClient? _stunClient;
    private readonly NatTraversalConfiguration _config;

    private readonly ConcurrentDictionary<string, NatMappingResult> _activeMappings = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private IPAddress? _lastKnownExternalAddress;
    private NatType? _detectedNatType;
    private bool _isRunning;
    private bool _disposed;

    public bool IsEnabled => _config.Enabled;
    public bool IsRunning => _isRunning;

    public event EventHandler<ExternalAddressChangedEventArgs>? ExternalAddressChanged;
    public event EventHandler<MappingExpiringEventArgs>? MappingExpiring;

    public NatTraversalManager(
        ILogger<NatTraversalManager> logger,
        NatTraversalConfiguration config,
        IUPnPService? upnpService = null,
        NatPmpClient? natPmpClient = null,
        StunClient? stunClient = null)
    {
        _logger = logger;
        _config = config;
        _upnpService = upnpService;
        _natPmpClient = natPmpClient;
        _stunClient = stunClient;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || _isRunning)
            return;

        _logger.LogInformation("Starting NAT traversal manager");

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            // Discover available NAT traversal methods
            var availableMethods = new List<NatMethod>();

            // Try UPnP
            if (_config.UpnpEnabled && _upnpService != null)
            {
                try
                {
                    var upnpAvailable = await _upnpService.IsAvailableAsync(cancellationToken);
                    if (upnpAvailable)
                    {
                        availableMethods.Add(NatMethod.UPnP);
                        _logger.LogInformation("UPnP is available");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check UPnP availability");
                }
            }

            // Try NAT-PMP
            if (_config.NatPmpEnabled && _natPmpClient != null)
            {
                try
                {
                    var pmpAvailable = await _natPmpClient.DiscoverGatewayAsync(cancellationToken);
                    if (pmpAvailable)
                    {
                        availableMethods.Add(NatMethod.NatPmp);
                        _logger.LogInformation("NAT-PMP is available");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check NAT-PMP availability");
                }
            }

            // Detect NAT type using STUN
            if (_config.StunEnabled && _stunClient != null && _config.StunServers.Any())
            {
                try
                {
                    var natTypeResult = await _stunClient.DetectNatTypeAsync(
                        _config.StunServers.ToArray(),
                        TimeSpan.FromSeconds(5),
                        cancellationToken);

                    _detectedNatType = natTypeResult.Type;
                    _lastKnownExternalAddress = natTypeResult.ExternalAddress;

                    _logger.LogInformation("NAT type detected: {NatType}, external address: {ExternalAddress}",
                        natTypeResult.Type, natTypeResult.ExternalAddress);

                    if (natTypeResult.Type != NatType.SymmetricNat)
                    {
                        availableMethods.Add(NatMethod.Stun);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to detect NAT type via STUN");
                }
            }

            _isRunning = true;

            _logger.LogInformation("NAT traversal manager started. Available methods: {Methods}",
                string.Join(", ", availableMethods));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _logger.LogInformation("Stopping NAT traversal manager");

        await _operationLock.WaitAsync();
        try
        {
            // Remove all active mappings
            foreach (var mapping in _activeMappings.Values.ToList())
            {
                try
                {
                    await RemoveMappingInternalAsync(mapping, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove mapping during shutdown: {Mapping}", mapping.Id);
                }
            }

            _activeMappings.Clear();
            _isRunning = false;

            _logger.LogInformation("NAT traversal manager stopped");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<NatMappingResult?> CreateMappingAsync(
        string protocol,
        int internalPort,
        int requestedExternalPort = 0,
        string description = "CreatioHelper",
        CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
        {
            _logger.LogWarning("NAT traversal manager is not running");
            return null;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            NatMappingResult? result = null;

            // Try UPnP first (most common)
            if (_config.UpnpEnabled && _upnpService != null && result == null)
            {
                result = await TryCreateUpnpMappingAsync(protocol, internalPort, requestedExternalPort, description, cancellationToken);
            }

            // Try NAT-PMP
            if (_config.NatPmpEnabled && _natPmpClient != null && result == null)
            {
                result = await TryCreateNatPmpMappingAsync(protocol, internalPort, requestedExternalPort, cancellationToken);
            }

            if (result != null)
            {
                result.Description = description;
                _activeMappings[result.Id] = result;

                _logger.LogInformation("Created NAT mapping: {Protocol} {InternalPort}→{ExternalPort} via {Method}",
                    protocol, internalPort, result.ExternalPort, result.Method);
            }
            else
            {
                _logger.LogWarning("Failed to create NAT mapping for {Protocol}:{InternalPort}", protocol, internalPort);
            }

            return result;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> RemoveMappingAsync(NatMappingResult mapping, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var result = await RemoveMappingInternalAsync(mapping, cancellationToken);

            if (result)
            {
                _activeMappings.TryRemove(mapping.Id, out _);
            }

            return result;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<List<NatMappingResult>> GetActiveMappingsAsync()
    {
        var now = DateTime.UtcNow;
        var activeMappings = _activeMappings.Values
            .Where(m => m.ExpiresAt > now)
            .ToList();

        // Check for expiring mappings
        foreach (var mapping in activeMappings.Where(m => m.ShouldRenew))
        {
            MappingExpiring?.Invoke(this, new MappingExpiringEventArgs(mapping, mapping.TimeToExpire));
        }

        return Task.FromResult(activeMappings);
    }

    public async Task<IPAddress?> GetExternalAddressAsync(CancellationToken cancellationToken = default)
    {
        // Try cached value first
        if (_lastKnownExternalAddress != null)
        {
            return _lastKnownExternalAddress;
        }

        // Try UPnP
        if (_upnpService != null)
        {
            try
            {
                var externalIp = await _upnpService.GetExternalIPAddressAsync();
                if (!string.IsNullOrEmpty(externalIp) && IPAddress.TryParse(externalIp, out var address))
                {
                    UpdateExternalAddress(address, NatMethod.UPnP);
                    return address;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get external address via UPnP");
            }
        }

        // Try NAT-PMP
        if (_natPmpClient != null)
        {
            try
            {
                var address = await _natPmpClient.GetExternalAddressAsync(cancellationToken);
                if (address != null)
                {
                    UpdateExternalAddress(address, NatMethod.NatPmp);
                    return address;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get external address via NAT-PMP");
            }
        }

        // Try STUN
        if (_stunClient != null && _config.StunServers.Any())
        {
            try
            {
                var result = await _stunClient.BindingRequestAsync(
                    _config.StunServers.First(),
                    cancellationToken: cancellationToken);

                if (result?.MappedEndPoint != null)
                {
                    UpdateExternalAddress(result.MappedEndPoint.Address, NatMethod.Stun);
                    return result.MappedEndPoint.Address;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get external address via STUN");
            }
        }

        return null;
    }

    public async Task<NatTypeResult?> DetectNatTypeAsync(CancellationToken cancellationToken = default)
    {
        if (_stunClient == null || !_config.StunServers.Any())
        {
            return null;
        }

        try
        {
            var result = await _stunClient.DetectNatTypeAsync(
                _config.StunServers.ToArray(),
                TimeSpan.FromSeconds(5),
                cancellationToken);

            _detectedNatType = result.Type;

            if (result.ExternalAddress != null)
            {
                UpdateExternalAddress(result.ExternalAddress, NatMethod.Stun);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect NAT type");
            return null;
        }
    }

    public NatTraversalManagerStatus GetStatus()
    {
        var status = new NatTraversalManagerStatus
        {
            IsEnabled = _config.Enabled,
            IsRunning = _isRunning,
            DetectedNatType = _detectedNatType,
            NatTypeDescription = _detectedNatType?.ToString(),
            ActiveMappingCount = _activeMappings.Count(m => !m.Value.IsExpired),
            LastUpdated = DateTime.UtcNow
        };

        if (_lastKnownExternalAddress != null)
        {
            status.ExternalAddresses.Add(_lastKnownExternalAddress);
        }

        // UPnP status
        if (_config.UpnpEnabled)
        {
            status.UPnP = new NatMethodStatus
            {
                Enabled = true,
                Available = _upnpService != null,
                ActiveMappings = _activeMappings.Values.Count(m => m.Method == NatMethod.UPnP && !m.IsExpired)
            };

            if (status.UPnP.Available)
            {
                status.AvailableMethods.Add(NatMethod.UPnP);
            }
        }

        // NAT-PMP status
        if (_config.NatPmpEnabled)
        {
            status.NatPmp = new NatMethodStatus
            {
                Enabled = true,
                Available = _natPmpClient != null,
                ActiveMappings = _activeMappings.Values.Count(m => m.Method == NatMethod.NatPmp && !m.IsExpired)
            };

            if (status.NatPmp.Available)
            {
                status.AvailableMethods.Add(NatMethod.NatPmp);
            }
        }

        // STUN status
        if (_config.StunEnabled)
        {
            status.Stun = new StunStatus
            {
                Enabled = true,
                Servers = _config.StunServers,
                ExternalAddress = _lastKnownExternalAddress,
                DetectedNatType = _detectedNatType
            };
        }

        // Determine preferred method
        if (status.AvailableMethods.Contains(NatMethod.UPnP))
        {
            status.PreferredMethod = NatMethod.UPnP;
        }
        else if (status.AvailableMethods.Contains(NatMethod.NatPmp))
        {
            status.PreferredMethod = NatMethod.NatPmp;
        }
        else if (status.AvailableMethods.Contains(NatMethod.Stun))
        {
            status.PreferredMethod = NatMethod.Stun;
        }

        return status;
    }

    private async Task<NatMappingResult?> TryCreateUpnpMappingAsync(
        string protocol,
        int internalPort,
        int requestedExternalPort,
        string description,
        CancellationToken cancellationToken)
    {
        if (_upnpService == null)
            return null;

        try
        {
            var externalPort = requestedExternalPort == 0 ? internalPort : requestedExternalPort;

            var success = await _upnpService.AddPortMappingAsync(
                externalPort,
                internalPort,
                protocol,
                description,
                0); // 0 = indefinite lease

            if (success)
            {
                var externalIpStr = await _upnpService.GetExternalIPAddressAsync();
                IPAddress.TryParse(externalIpStr, out var externalIp);

                return new NatMappingResult
                {
                    Protocol = protocol,
                    InternalPort = internalPort,
                    ExternalPort = externalPort,
                    ExternalAddress = externalIp,
                    Method = NatMethod.UPnP,
                    Description = description,
                    ExpiresAt = DateTime.UtcNow.AddHours(24) // UPnP typically 24h lease
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create UPnP mapping");
        }

        return null;
    }

    private async Task<NatMappingResult?> TryCreateNatPmpMappingAsync(
        string protocol,
        int internalPort,
        int requestedExternalPort,
        CancellationToken cancellationToken)
    {
        if (_natPmpClient == null)
            return null;

        try
        {
            var mapping = await _natPmpClient.CreateMappingAsync(
                protocol,
                internalPort,
                requestedExternalPort,
                7200, // 2 hours
                cancellationToken);

            if (mapping != null)
            {
                var externalIp = await _natPmpClient.GetExternalAddressAsync(cancellationToken);

                return new NatMappingResult
                {
                    Protocol = protocol,
                    InternalPort = mapping.InternalPort,
                    ExternalPort = mapping.ExternalPort,
                    ExternalAddress = externalIp,
                    Method = NatMethod.NatPmp,
                    ExpiresAt = mapping.ExpiresAt
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create NAT-PMP mapping");
        }

        return null;
    }

    private async Task<bool> RemoveMappingInternalAsync(NatMappingResult mapping, CancellationToken cancellationToken)
    {
        try
        {
            switch (mapping.Method)
            {
                case NatMethod.UPnP when _upnpService != null:
                    return await _upnpService.DeletePortMappingAsync(mapping.ExternalPort, mapping.Protocol);

                case NatMethod.NatPmp when _natPmpClient != null:
                    return await _natPmpClient.DeleteMappingAsync(mapping.Protocol, mapping.InternalPort, cancellationToken);

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove mapping {MappingId}", mapping.Id);
            return false;
        }
    }

    private void UpdateExternalAddress(IPAddress newAddress, NatMethod method)
    {
        var oldAddress = _lastKnownExternalAddress;

        if (oldAddress == null || !oldAddress.Equals(newAddress))
        {
            _lastKnownExternalAddress = newAddress;
            ExternalAddressChanged?.Invoke(this, new ExternalAddressChangedEventArgs(oldAddress, newAddress, method));

            _logger.LogInformation("External address updated: {OldAddress} → {NewAddress} via {Method}",
                oldAddress, newAddress, method);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopAsync().GetAwaiter().GetResult();
            _operationLock.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Configuration for NAT traversal
/// </summary>
public class NatTraversalConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool UpnpEnabled { get; set; } = true;
    public bool NatPmpEnabled { get; set; } = true;
    public bool StunEnabled { get; set; } = true;

    public List<string> StunServers { get; set; } = new();

    public int MappingRenewalMinutes { get; set; } = 30;
    public int StunKeepaliveIntervalSeconds { get; set; } = 30;
}
