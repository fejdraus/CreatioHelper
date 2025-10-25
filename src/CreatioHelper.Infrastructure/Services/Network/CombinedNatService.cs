using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Network.UPnP;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;

namespace CreatioHelper.Infrastructure.Services.Network;

/// <summary>
/// Combined NAT traversal service that uses both UPnP and PMP protocols
/// Tries UPnP first, then falls back to PMP if UPnP fails
/// </summary>
public interface ICombinedNatService : IDisposable
{
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<NatMapping?> CreateMappingAsync(string protocol, int internalPort, int externalPort = 0, string description = "CreatioHelper");
    Task<bool> RemoveMappingAsync(NatMapping mapping);
    Task<List<NatMapping>> GetActiveMappingsAsync();
    Task<NatTraversalStatus> GetStatusAsync();
    bool IsEnabled { get; }
}

public class NatTraversalStatus
{
    public bool IsEnabled { get; set; }
    public bool IsStarted { get; set; }
    public bool UpnpAvailable { get; set; }
    public bool PmpAvailable { get; set; }
    public int UpnpDeviceCount { get; set; }
    public int PmpGatewayCount { get; set; }
    public int ActiveMappingCount { get; set; }
    public List<string> ExternalIPs { get; set; } = new();
    public DateTime LastDiscovery { get; set; }
    public string PreferredMethod { get; set; } = string.Empty;
}

public class CombinedNatService : ICombinedNatService, IDisposable
{
    private readonly ILogger<CombinedNatService> _logger;
    private readonly SyncConfiguration _config;
    private readonly IUPnPService _upnpService;
    private readonly IPmpService? _pmpService;
    private readonly List<NatMapping> _activeMappings = new();
    private volatile bool _isEnabled;
    private volatile bool _isStarted;
    private DateTime _lastDiscovery = DateTime.MinValue;

    public bool IsEnabled => _isEnabled && _isStarted;

    public CombinedNatService(
        ILogger<CombinedNatService> logger, 
        IOptions<SyncConfiguration> config,
        IUPnPService upnpService,
        IPmpService? pmpService = null)
    {
        _logger = logger;
        _config = config.Value;
        _upnpService = upnpService;
        _pmpService = pmpService;
        _isEnabled = _config.NatTraversal?.Enabled ?? false;
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_isEnabled || _isStarted)
            return _isStarted;

        _logger.LogInformation("Starting combined NAT traversal service (UPnP + PMP)");

        try
        {
            bool upnpStarted = false;
            bool pmpStarted = false;

            // Start UPnP service if enabled
            if (_config.NatTraversal?.UpnpEnabled == true)
            {
                try
                {
                    upnpStarted = await _upnpService.IsAvailableAsync(cancellationToken);
                    if (upnpStarted)
                    {
                        _logger.LogDebug("UPnP service is available");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start UPnP service");
                }
            }

            // Start PMP service if enabled (placeholder for now)
            if (_config.NatTraversal?.PmpEnabled == true && _pmpService != null)
            {
                _logger.LogDebug("PMP service support not fully implemented yet");
                pmpStarted = false;
            }

            var anyStarted = upnpStarted || pmpStarted;

            if (anyStarted)
            {
                _isStarted = true;
                _lastDiscovery = DateTime.UtcNow;
                
                var status = await GetStatusAsync();
                _logger.LogInformation("Combined NAT service started - UPnP: {UpnpAvailable} ({UpnpDevices} devices), PMP: {PmpAvailable} ({PmpGateways} gateways)", 
                    status.UpnpAvailable, status.UpnpDeviceCount, status.PmpAvailable, status.PmpGatewayCount);
                
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to start any NAT traversal services");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start combined NAT service");
            return false;
        }
    }

    private async Task<bool> StartServiceSafelyAsync(object service, string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            if (service is INatService natService)
            {
                var result = await natService.StartAsync(cancellationToken);
                if (result)
                {
                    _logger.LogDebug("{ServiceName} service started successfully", serviceName);
                }
                else
                {
                    _logger.LogDebug("{ServiceName} service failed to start", serviceName);
                }
                return result;
            }
            else if (service is IPmpService pmpService)
            {
                var result = await pmpService.StartAsync(cancellationToken);
                if (result)
                {
                    _logger.LogDebug("{ServiceName} service started successfully", serviceName);
                }
                else
                {
                    _logger.LogDebug("{ServiceName} service failed to start", serviceName);
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error starting {ServiceName} service", serviceName);
        }
        
        return false;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStarted)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping combined NAT traversal service");

        // UPnP service doesn't need explicit stopping - it's stateless
        _logger.LogDebug("UPnP service is stateless, no explicit stop needed");

        // PMP service stopping not implemented yet
        if (_pmpService != null)
        {
            _logger.LogDebug("PMP service stopping not implemented yet");
        }

        _isStarted = false;
        _logger.LogInformation("Combined NAT service stopped");
        
        return Task.CompletedTask;
    }

    public async Task<NatMapping?> CreateMappingAsync(string protocol, int internalPort, int externalPort = 0, string description = "CreatioHelper")
    {
        if (!_isStarted)
        {
            _logger.LogWarning("Combined NAT service is not started, cannot create mapping");
            return null;
        }

        // Try UPnP first if enabled and available
        if (_config.NatTraversal?.UpnpEnabled == true)
        {
            try
            {
                var actualExternalPort = externalPort == 0 ? internalPort : externalPort;
                var success = await _upnpService.AddPortMappingAsync(actualExternalPort, internalPort, protocol, description, 0);
                
                if (success)
                {
                    var mapping = new NatMapping
                    {
                        Id = Guid.NewGuid().ToString(),
                        Protocol = protocol,
                        InternalPort = internalPort,
                        ExternalPort = actualExternalPort,
                        InternalIP = IPAddress.Parse("127.0.0.1"), // Local IP will be determined by UPnP service
                        ExternalIP = IPAddress.Any, // Will be filled by GetExternalIPAddress
                        Description = description,
                        ExpiresAt = DateTime.UtcNow.AddHours(24), // 24 hour lease
                        DeviceId = "upnp"
                    };

                    // Try to get external IP
                    var externalIP = await _upnpService.GetExternalIPAddressAsync();
                    if (!string.IsNullOrEmpty(externalIP) && IPAddress.TryParse(externalIP, out var ip))
                    {
                        mapping.ExternalIP = ip;
                    }

                    _activeMappings.Add(mapping);
                    _logger.LogInformation("Successfully created UPnP mapping: {Protocol}:{InternalPort}→{ExternalPort}", 
                        protocol, internalPort, actualExternalPort);
                    return mapping;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "UPnP mapping creation failed");
            }
        }

        // PMP fallback (not implemented yet)
        if (_config.NatTraversal?.PmpEnabled == true && _pmpService != null)
        {
            _logger.LogDebug("PMP mapping creation not implemented yet");
        }

        _logger.LogWarning("Failed to create NAT mapping using any available method");
        return null;
    }

    public async Task<bool> RemoveMappingAsync(NatMapping mapping)
    {
        if (!_isStarted)
            return false;

        bool result = false;

        // Try to remove from UPnP service first
        if (_config.NatTraversal?.UpnpEnabled == true)
        {
            try
            {
                result = await _upnpService.DeletePortMappingAsync(mapping.ExternalPort, mapping.Protocol);
                if (result)
                {
                    _activeMappings.RemoveAll(m => m.Id == mapping.Id);
                    _logger.LogInformation("Successfully removed UPnP mapping: {Protocol}:{ExternalPort}", 
                        mapping.Protocol, mapping.ExternalPort);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to remove UPnP mapping");
            }
        }

        // Try PMP service if available (not implemented yet)
        if (_config.NatTraversal?.PmpEnabled == true && _pmpService != null)
        {
            _logger.LogDebug("PMP mapping removal not implemented yet");
        }

        _logger.LogWarning("Failed to remove NAT mapping: {Protocol}:{ExternalPort}", 
            mapping.Protocol, mapping.ExternalPort);
        return false;
    }

    public Task<List<NatMapping>> GetActiveMappingsAsync()
    {
        // Return our tracked active mappings
        var result = new List<NatMapping>(_activeMappings);
        
        // Remove any expired mappings
        var expiredMappings = result.Where(m => m.IsExpired).ToList();
        foreach (var expired in expiredMappings)
        {
            _activeMappings.RemoveAll(m => m.Id == expired.Id);
            result.Remove(expired);
            
            // Try to clean up the expired mapping from the router
            _ = Task.Run(async () =>
            {
                try
                {
                    await _upnpService.DeletePortMappingAsync(expired.ExternalPort, expired.Protocol);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to clean up expired mapping {Protocol}:{ExternalPort}", 
                        expired.Protocol, expired.ExternalPort);
                }
            });
        }
        
        return Task.FromResult(result);
    }

    public async Task<NatTraversalStatus> GetStatusAsync()
    {
        var status = new NatTraversalStatus
        {
            IsEnabled = _isEnabled,
            IsStarted = _isStarted,
            LastDiscovery = _lastDiscovery
        };

        if (!_isStarted)
            return status;

        // Get UPnP status
        if (_config.NatTraversal?.UpnpEnabled == true)
        {
            try
            {
                status.UpnpAvailable = await _upnpService.IsAvailableAsync();
                if (status.UpnpAvailable)
                {
                    var devices = await _upnpService.DiscoverDevicesAsync(TimeSpan.FromSeconds(2));
                    status.UpnpDeviceCount = devices.Count;
                    
                    // Get external IP
                    var externalIP = await _upnpService.GetExternalIPAddressAsync();
                    if (!string.IsNullOrEmpty(externalIP))
                    {
                        status.ExternalIPs.Add(externalIP);
                    }
                    
                    // Count active mappings
                    status.ActiveMappingCount = _activeMappings.Count(m => !m.IsExpired);
                    
                    status.PreferredMethod = "UPnP";
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error getting UPnP status");
            }
        }

        // Get PMP status (placeholder)
        if (_config.NatTraversal?.PmpEnabled == true && _pmpService != null)
        {
            status.PmpAvailable = false; // Not implemented yet
            status.PmpGatewayCount = 0;
            
            if (string.IsNullOrEmpty(status.PreferredMethod))
            {
                status.PreferredMethod = "PMP (not implemented)";
            }
        }

        return status;
    }

    public void Dispose()
    {
        if (_isStarted)
        {
            _ = StopAsync();
        }
        
        // Clean up active mappings
        foreach (var mapping in _activeMappings.ToList())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RemoveMappingAsync(mapping);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error cleaning up mapping during dispose");
                }
            });
        }
        
        // UPnP service will be disposed by DI container (it's a singleton)
        _activeMappings.Clear();
    }
}