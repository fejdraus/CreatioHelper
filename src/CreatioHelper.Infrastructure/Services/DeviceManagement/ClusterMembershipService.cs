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

    public async Task<List<ClusterMember>> BuildRosterAsync(CancellationToken cancellationToken = default)
    {
        var roster = new List<ClusterMember> { GetLocalMember() };

        if (!_config.ShareRoster)
        {
            return roster;
        }

        var devices = await _syncEngine.GetDevicesAsync();
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(device.DeviceId, _syncEngine.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_configManager.IsDeviceIgnored(device.DeviceId))
            {
                continue;
            }

            roster.Add(new ClusterMember
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                Addresses = device.Addresses.ToList(),
                ApiAddress = ResolveApiAddress(device.DeviceId, device.Addresses) ?? "",
                AdmittedAt = device.AdmittedAt ?? DateTime.MinValue,
                StateVersion = device.StateVersion
            });
        }

        return roster;
    }

    public async Task<ClusterJoinDecision> RequestJoinAsync(
        ClusterMember remote,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(remote.DeviceId))
        {
            var devices = await _syncEngine.GetDevicesAsync();
            var alreadyKnown = !_configManager.IsDeviceIgnored(remote.DeviceId) && devices.Any(d =>
                string.Equals(d.DeviceId, remote.DeviceId, StringComparison.OrdinalIgnoreCase));

            if (alreadyKnown)
            {
                await MergeMemberAsync(remote, cancellationToken);
                return new ClusterJoinDecision
                {
                    Pending = false,
                    Ack = new ClusterPairingAck
                    {
                        Self = GetLocalMember(),
                        Roster = await BuildRosterAsync(cancellationToken),
                        Tombstones = await GetTombstonesAsync()
                    }
                };
            }

            _logger.LogInformation(
                "Cluster join request from {DeviceId} held for approval",
                remote.DeviceId);
            return new ClusterJoinDecision { Pending = true };
        }

        return new ClusterJoinDecision
        {
            Pending = false,
            Ack = await AcceptPairedDeviceAsync(remote, cancellationToken)
        };
    }

    public async Task<ClusterPairingAck> AcceptPairedDeviceAsync(
        ClusterMember remote,
        CancellationToken cancellationToken = default)
    {
        if (remote.AdmittedAt == default)
        {
            remote.AdmittedAt = DateTime.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(remote.DeviceId))
        {
            remote.StateVersion = await _configManager.GetMaxDeviceVersionAsync(remote.DeviceId) + 1;
            await _configManager.RemoveIgnoredDeviceAsync(remote.DeviceId);
        }

        await MergeMemberAsync(remote, cancellationToken);

        return new ClusterPairingAck
        {
            Self = GetLocalMember(),
            Roster = await BuildRosterAsync(cancellationToken),
            Tombstones = await GetTombstonesAsync()
        };
    }

    public async Task<ClusterPairingResult> PairWithAsync(
        string apiBaseUrl,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new ClusterPairingResult { Success = false, Error = "Cluster key is not enabled" };
        }

        var baseUrl = NormalizeApiAddress(apiBaseUrl);
        if (baseUrl == null)
        {
            return new ClusterPairingResult { Success = false, Error = $"Malformed address '{apiBaseUrl}'" };
        }

        try
        {
            var client = _httpClientFactory.CreateClient("ClusterKey");
            client.Timeout = TimeSpan.FromSeconds(10);

            var local = GetLocalMember();

            var challengeResponse = await client.PostAsJsonAsync(
                $"{baseUrl}/rest/cluster/key/challenge",
                new { deviceId = local.DeviceId },
                cancellationToken);

            if (!challengeResponse.IsSuccessStatusCode)
            {
                return new ClusterPairingResult
                {
                    Success = false,
                    Error = $"Challenge rejected with {(int)challengeResponse.StatusCode}"
                };
            }

            var challenge = await challengeResponse.Content
                .ReadFromJsonAsync<ChallengeResponseDto>(cancellationToken);

            if (challenge == null || string.IsNullOrEmpty(challenge.Nonce) || string.IsNullOrEmpty(challenge.DeviceId))
            {
                return new ClusterPairingResult { Success = false, Error = "Empty challenge response" };
            }

            if (string.Equals(challenge.DeviceId, local.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return new ClusterPairingResult { Success = false, Error = "Target is this agent itself" };
            }

            var proof = _clusterKeyService.ComputeProof(challenge.Nonce, local.DeviceId, challenge.DeviceId);

            var verifyResponse = await client.PostAsJsonAsync(
                $"{baseUrl}/rest/cluster/key/verify",
                new
                {
                    nonce = challenge.Nonce,
                    deviceId = local.DeviceId,
                    hmacProof = proof,
                    deviceName = local.DeviceName,
                    addresses = local.Addresses,
                    apiAddress = local.ApiAddress
                },
                cancellationToken);

            if (!verifyResponse.IsSuccessStatusCode)
            {
                return new ClusterPairingResult
                {
                    Success = false,
                    Error = $"Verification rejected with {(int)verifyResponse.StatusCode}"
                };
            }

            var ack = await verifyResponse.Content.ReadFromJsonAsync<ClusterPairingAck>(cancellationToken);

            if (ack?.Pending == true)
            {
                return new ClusterPairingResult
                {
                    Success = false,
                    Pending = true,
                    Error = "Awaiting approval on remote node"
                };
            }

            var remote = ack?.Self ?? new ClusterMember { DeviceId = challenge.DeviceId };
            if (string.IsNullOrWhiteSpace(remote.DeviceId))
            {
                remote.DeviceId = challenge.DeviceId;
            }

            if (string.IsNullOrWhiteSpace(remote.ApiAddress))
            {
                remote.ApiAddress = baseUrl;
            }

            RememberApiAddress(remote.DeviceId, remote.ApiAddress);

            return new ClusterPairingResult
            {
                Success = true,
                Remote = remote,
                Roster = ack?.Roster ?? new List<ClusterMember>(),
                Tombstones = ack?.Tombstones ?? new List<ClusterTombstone>()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cluster pairing with {Target} failed", baseUrl);
            return new ClusterPairingResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<ClusterJoinReport> JoinClusterAsync(CancellationToken cancellationToken = default)
    {
        var report = new ClusterJoinReport();

        if (!IsEnabled)
        {
            return report;
        }

        var retentionDays = Math.Max(1, _config.TombstoneRetentionDays);
        await _configManager.PruneIgnoredDevicesAsync(DateTime.UtcNow.AddDays(-retentionDays));

        var localId = _syncEngine.DeviceId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var target in await CollectInitialTargetsAsync())
        {
            queue.Enqueue(target);
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (visited.Count >= Math.Max(1, _config.MaxJoinTargets))
            {
                report.TargetLimitReached = true;
                _logger.LogWarning("Cluster join stopped at the {Limit} target limit", _config.MaxJoinTargets);
                break;
            }

            var target = NormalizeApiAddress(queue.Dequeue());
            if (target == null || !visited.Add(target))
            {
                continue;
            }

            report.TargetsContacted++;

            var result = await PairWithAsync(target, cancellationToken);
            if (!result.Success || result.Remote == null)
            {
                report.FailedTargets.Add(target);
                continue;
            }

            report.TargetsPaired++;

            await ApplyTombstonesAsync(result.Tombstones, cancellationToken);

            if (await MergeMemberAsync(result.Remote, cancellationToken))
            {
                report.AddedDeviceIds.Add(result.Remote.DeviceId);
            }

            foreach (var member in result.Roster)
            {
                if (string.IsNullOrWhiteSpace(member.DeviceId) ||
                    string.Equals(member.DeviceId, localId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (await MergeMemberAsync(member, cancellationToken))
                {
                    report.AddedDeviceIds.Add(member.DeviceId);
                }

                var memberApi = member.ApiAddress;
                if (string.IsNullOrWhiteSpace(memberApi))
                {
                    memberApi = ResolveApiAddress(member.DeviceId, member.Addresses);
                }

                if (!string.IsNullOrWhiteSpace(memberApi))
                {
                    queue.Enqueue(memberApi!);
                }
            }
        }

        if (report.TargetsContacted > 0)
        {
            _logger.LogInformation(
                "Cluster join pass finished: {Paired}/{Contacted} agents paired, {Added} devices added",
                report.TargetsPaired, report.TargetsContacted, report.AddedDeviceIds.Count);
        }

        return report;
    }

    public async Task<List<string>> MergeRosterAsync(
        IEnumerable<ClusterMember> members,
        CancellationToken cancellationToken = default)
    {
        var added = new List<string>();

        foreach (var member in members)
        {
            if (await MergeMemberAsync(member, cancellationToken))
            {
                added.Add(member.DeviceId);
            }
        }

        return added;
    }

    public async Task<List<ClusterTombstone>> GetTombstonesAsync()
    {
        var ignored = await _configManager.GetIgnoredDevicesAsync();
        return ignored
            .Where(d => !string.IsNullOrWhiteSpace(d.Id))
            .Select(d => new ClusterTombstone
            {
                DeviceId = d.Id,
                DeviceName = d.Name,
                DeletedAt = d.Time,
                StateVersion = d.StateVersion
            })
            .ToList();
    }

    public async Task ApplyTombstonesAsync(
        IEnumerable<ClusterTombstone> tombstones,
        CancellationToken cancellationToken = default)
    {
        if (tombstones == null)
        {
            return;
        }

        var localId = _syncEngine.DeviceId;

        foreach (var tombstone in tombstones)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(tombstone.DeviceId) ||
                string.Equals(tombstone.DeviceId, localId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var deletedAt = tombstone.DeletedAt == default ? DateTime.UtcNow : tombstone.DeletedAt;

            var devices = await _syncEngine.GetDevicesAsync();
            var present = devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, tombstone.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (present != null && present.StateVersion > tombstone.StateVersion)
            {
                _logger.LogInformation(
                    "Ignoring stale tombstone for {DeviceId} (present version {PresentVersion} newer than deleted version {DeletedVersion})",
                    tombstone.DeviceId, present.StateVersion, tombstone.StateVersion);
                continue;
            }

            await _syncEngine.RemoveDeviceAsync(tombstone.DeviceId);
            _pendingService.RemovePendingDevice(tombstone.DeviceId);

            var name = string.IsNullOrWhiteSpace(tombstone.DeviceName) ? tombstone.DeviceId : tombstone.DeviceName;
            await _configManager.AddIgnoredDeviceAsync(tombstone.DeviceId, name, deletedAt, tombstone.StateVersion);
        }
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
            var ignored = await _configManager.GetIgnoredDevicesAsync();
            var tomb = ignored.FirstOrDefault(d =>
                string.Equals(d.Id, member.DeviceId, StringComparison.OrdinalIgnoreCase));
            var tombstoneVersion = tomb?.StateVersion ?? long.MaxValue;

            if (member.StateVersion <= tombstoneVersion)
            {
                _logger.LogDebug("Skipping tombstoned device {DeviceId} during roster merge", member.DeviceId);
                return false;
            }

            _logger.LogInformation(
                "Approval overrides tombstone for {DeviceId} (version {MemberVersion} newer than deleted version {DeletedVersion})",
                member.DeviceId, member.StateVersion, tombstoneVersion);
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

            var addressesChanged = merged.Count != existing.Addresses.Count;
            if (addressesChanged)
            {
                existing.Addresses = merged;
            }

            var adoptsVersion = member.StateVersion > existing.StateVersion;
            if (adoptsVersion)
            {
                existing.StateVersion = member.StateVersion;
                existing.AdmittedAt = member.AdmittedAt == default ? existing.AdmittedAt : member.AdmittedAt;
            }

            if (addressesChanged || adoptsVersion)
            {
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
        admitted.StateVersion = member.StateVersion;
        await _configManager.UpsertDeviceAsync(admitted);

        _logger.LogInformation("Cluster key admitted device {DeviceId} ({DeviceName})", member.DeviceId, name);
        return true;
    }

    private async Task<List<string>> CollectInitialTargetsAsync()
    {
        var targets = new List<string>();

        foreach (var seed in _config.SeedAddresses)
        {
            if (!string.IsNullOrWhiteSpace(seed))
            {
                targets.Add(seed);
            }
        }

        var devices = await _syncEngine.GetDevicesAsync();
        foreach (var device in devices)
        {
            var api = ResolveApiAddress(device.DeviceId, device.Addresses);
            if (!string.IsNullOrWhiteSpace(api))
            {
                targets.Add(api!);
            }
        }

        var localId = _syncEngine.DeviceId;
        foreach (var discovered in _discoveryManager.GetDiscoveredDevices())
        {
            if (string.Equals(discovered.DeviceId, localId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var api = ResolveApiAddress(discovered.DeviceId, discovered.Addresses);
            if (!string.IsNullOrWhiteSpace(api))
            {
                targets.Add(api!);
            }
        }

        return targets;
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
