using System.Collections.Concurrent;
using System.Net.Http.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Network.Discovery;
using CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

public class ClusterMembershipService : IClusterMembershipService
{
    private readonly ILogger<ClusterMembershipService> _logger;
    private readonly IClusterKeyService _clusterKeyService;
    private readonly ISyncEngine _syncEngine;
    private readonly CreatioHelper.Application.Interfaces.IConfigurationManager _configManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDiscoveryManager _discoveryManager;
    private readonly IPendingService _pendingService;
    private readonly ClusterKeyConfiguration _config;
    private readonly SyncConfiguration _syncConfig;
    private readonly int _agentHttpPort;
    private readonly bool _clusterMode;

    private readonly ConcurrentDictionary<string, string> _apiAddresses = new(StringComparer.OrdinalIgnoreCase);

    public ClusterMembershipService(
        ILogger<ClusterMembershipService> logger,
        IClusterKeyService clusterKeyService,
        ISyncEngine syncEngine,
        CreatioHelper.Application.Interfaces.IConfigurationManager configManager,
        IHttpClientFactory httpClientFactory,
        IDiscoveryManager discoveryManager,
        IPendingService pendingService,
        IOptions<ClusterKeyConfiguration> config,
        SyncConfiguration syncConfig,
        IConfiguration configuration)
    {
        _logger = logger;
        _clusterKeyService = clusterKeyService;
        _syncEngine = syncEngine;
        _configManager = configManager;
        _httpClientFactory = httpClientFactory;
        _discoveryManager = discoveryManager;
        _pendingService = pendingService;
        _config = config.Value;
        _syncConfig = syncConfig;
        _agentHttpPort = ResolveAgentPort(configuration);
        _clusterMode = CreatioHelper.Infrastructure.Services.Configuration.ClusterModeSettings.IsClusterMode(configuration);
    }

    public bool IsEnabled => _clusterKeyService.IsEnabled && _clusterMode;

    public ClusterMember GetLocalMember()
    {
        return new ClusterMember
        {
            DeviceId = _syncEngine.DeviceId,
            DeviceName = string.IsNullOrWhiteSpace(_syncConfig.DeviceName)
                ? Environment.MachineName
                : _syncConfig.DeviceName,
            Addresses = BuildLocalAddresses(),
            ApiAddress = BuildLocalApiAddress(),
            AdmittedAt = DateTime.UtcNow
        };
    }



    public async Task AcceptPairedDeviceAsync(
        ClusterMember remote,
        CancellationToken cancellationToken = default)
    {
        if (remote.AdmittedAt == default)
        {
            remote.AdmittedAt = DateTime.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(remote.DeviceId))
        {
            await _configManager.RemoveIgnoredDeviceAsync(remote.DeviceId);
        }

        await MergeMemberAsync(remote, cancellationToken);
    }






    public string? ResolveApiAddress(string deviceId, IEnumerable<string>? addresses = null)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) && _apiAddresses.TryGetValue(deviceId, out var known))
        {
            return known;
        }

        if (addresses == null)
        {
            return null;
        }

        foreach (var address in addresses)
        {
            var host = ExtractHost(address);
            if (host != null)
            {
                return $"http://{host}:{_agentHttpPort}";
            }
        }

        return null;
    }

    private async Task<bool> MergeMemberAsync(ClusterMember member, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(member.DeviceId) ||
            string.Equals(member.DeviceId, _syncEngine.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_configManager.IsDeviceIgnored(member.DeviceId))
        {
            await _configManager.RemoveIgnoredDeviceAsync(member.DeviceId);
        }

        cancellationToken.ThrowIfCancellationRequested();

        RememberApiAddress(member.DeviceId, member.ApiAddress);
        _pendingService.RemovePendingDevice(member.DeviceId);

        var devices = await _syncEngine.GetDevicesAsync();
        var existing = devices.FirstOrDefault(d =>
            string.Equals(d.DeviceId, member.DeviceId, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var merged = existing.Addresses
                .Concat(member.Addresses)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (merged.Count != existing.Addresses.Count)
            {
                existing.Addresses = merged;
                if (member.AdmittedAt != default)
                {
                    existing.AdmittedAt = member.AdmittedAt;
                }
                await _configManager.UpsertDeviceAsync(existing);
            }

            return false;
        }

        var name = string.IsNullOrWhiteSpace(member.DeviceName) ? member.DeviceId : member.DeviceName;

        var admitted = await _syncEngine.AddDeviceAsync(
            member.DeviceId,
            name,
            addresses: member.Addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList());

        admitted.AdmittedAt = member.AdmittedAt == default ? DateTime.UtcNow : member.AdmittedAt;
        await _configManager.UpsertDeviceAsync(admitted);

        _logger.LogInformation("Cluster key admitted device {DeviceId} ({DeviceName})", member.DeviceId, name);
        return true;
    }


    private void RememberApiAddress(string deviceId, string? apiAddress)
    {
        var normalized = NormalizeApiAddress(apiAddress);
        if (string.IsNullOrWhiteSpace(deviceId) || normalized == null)
        {
            return;
        }

        _apiAddresses[deviceId] = normalized;
    }

    private List<string> BuildLocalAddresses()
    {
        var host = ExtractHost(BuildLocalApiAddress()) ?? ResolveLocalHost();
        var addresses = new List<string>();

        foreach (var listen in _syncConfig.ListenAddresses)
        {
            if (!Uri.TryCreate(listen, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var isWildcard = uri.Host is "0.0.0.0" or "::" or "[::]" or "*";
            addresses.Add(isWildcard ? $"{uri.Scheme}://{host}:{uri.Port}" : listen);
        }

        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string BuildLocalApiAddress()
    {
        var advertised = NormalizeApiAddress(_config.AdvertisedApiAddress);
        return advertised ?? $"http://{ResolveLocalHost()}:{_agentHttpPort}";
    }

    private static string ResolveLocalHost()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                        System.Net.IPAddress.IsLoopback(ip))
                    {
                        continue;
                    }

                    var value = ip.ToString();
                    if (IsRoutableHost(value) && !value.StartsWith("169.254", StringComparison.Ordinal))
                    {
                        return value;
                    }
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }

        return Environment.MachineName;
    }

    private static string? NormalizeApiAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var candidate = address.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (!IsRoutableHost(uri.Host))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static string? ExtractHost(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return IsRoutableHost(uri.Host) ? uri.Host : null;
    }

    private static bool IsRoutableHost(string host)
    {
        var trimmed = host.Trim('[', ']');

        if (trimmed is "*" or "0.0.0.0" or "::" or "::0")
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(trimmed, out var ip))
        {
            return !ip.Equals(System.Net.IPAddress.Any) && !ip.Equals(System.Net.IPAddress.IPv6Any);
        }

        return true;
    }

    private static int ResolveAgentPort(IConfiguration configuration)
    {
        var portStr = configuration["ClusterKey:AgentHttpPort"];
        if (int.TryParse(portStr, out var port))
        {
            return port;
        }

        var urls = configuration["Urls"] ?? configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrEmpty(urls))
        {
            foreach (var url in urls.Split(';'))
            {
                if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "http")
                {
                    return uri.Port;
                }
            }
        }

        return 5000;
    }

    private class ChallengeResponseDto
    {
        public string Nonce { get; set; } = "";
        public string DeviceId { get; set; } = "";
    }
}
