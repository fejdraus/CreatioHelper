using System.Net.Http.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Network.Discovery;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Handles automatic device acceptance via cluster key HMAC challenge-response.
/// Subscribes to DiscoveryManager.DeviceDiscovered events and initiates pairing
/// with remote agents that share the same cluster key.
/// </summary>
public class ClusterKeyAutoAcceptHandler : IDisposable
{
    private readonly ILogger<ClusterKeyAutoAcceptHandler> _logger;
    private readonly IClusterKeyService _clusterKeyService;
    private readonly ISyncEngine _syncEngine;
    private readonly IDiscoveryManager _discoveryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _agentHttpPort;
    private bool _disposed;

    public ClusterKeyAutoAcceptHandler(
        ILogger<ClusterKeyAutoAcceptHandler> logger,
        IClusterKeyService clusterKeyService,
        ISyncEngine syncEngine,
        IDiscoveryManager discoveryManager,
        IHttpClientFactory httpClientFactory,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _logger = logger;
        _clusterKeyService = clusterKeyService;
        _syncEngine = syncEngine;
        _discoveryManager = discoveryManager;
        _httpClientFactory = httpClientFactory;
        _agentHttpPort = ResolveAgentPort(configuration);

        _discoveryManager.DeviceDiscovered += OnDeviceDiscovered;
    }

    private async void OnDeviceDiscovered(object? sender, DeviceDiscoveredArgs e)
    {
        try
        {
            if (!_clusterKeyService.IsEnabled)
                return;

            // Skip if device is already known
            var devices = await _syncEngine.GetDevicesAsync();
            if (devices.Any(d => d.DeviceId == e.DeviceId))
                return;

            _logger.LogInformation("New device {DeviceId} discovered, attempting cluster key auto-pairing", e.DeviceId);

            // Find a reachable HTTP address from discovered addresses
            var httpAddress = ResolveHttpAddress(e.Addresses);
            if (httpAddress == null)
            {
                _logger.LogDebug("No HTTP address found for device {DeviceId}, falling back to pending flow", e.DeviceId);
                return;
            }

            var success = await TryChallengeResponseAsync(e.DeviceId, httpAddress);

            if (success)
            {
                _logger.LogInformation("Cluster key auto-pairing succeeded for device {DeviceId}", e.DeviceId);

                // Accept the device via SyncEngine
                await _syncEngine.AddDeviceAsync(e.DeviceId, e.DeviceId,
                    addresses: e.Addresses.Select(a => a.Address).ToList());
            }
            else
            {
                _logger.LogDebug("Cluster key auto-pairing failed for device {DeviceId}, standard pending flow applies", e.DeviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cluster key auto-pairing for device {DeviceId}", e.DeviceId);
        }
    }

    /// <summary>
    /// Perform the challenge-response handshake with a remote agent.
    /// 1. POST /rest/cluster/key/challenge with our deviceId -> get nonce + remoteDeviceId
    /// 2. Compute HMAC proof
    /// 3. POST /rest/cluster/key/verify with proof -> success means same cluster key
    /// </summary>
    private async Task<bool> TryChallengeResponseAsync(string remoteDeviceId, string httpBaseUrl)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ClusterKey");
            client.Timeout = TimeSpan.FromSeconds(10);

            // Step 1: Request challenge
            var challengePayload = new { deviceId = _syncEngine.DeviceId };
            var challengeResponse = await client.PostAsJsonAsync(
                $"{httpBaseUrl}/rest/cluster/key/challenge", challengePayload);

            if (!challengeResponse.IsSuccessStatusCode)
            {
                _logger.LogDebug("Challenge request failed for {DeviceId}: {Status}",
                    remoteDeviceId, challengeResponse.StatusCode);
                return false;
            }

            var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponseDto>();
            if (challenge == null || string.IsNullOrEmpty(challenge.Nonce))
                return false;

            // Step 2: Compute HMAC proof
            // HMAC-SHA256(clusterKey, nonce + localDeviceId + remoteDeviceId)
            var proof = _clusterKeyService.ComputeProof(
                challenge.Nonce, _syncEngine.DeviceId, challenge.DeviceId);

            // Step 3: Send verification
            var verifyPayload = new
            {
                nonce = challenge.Nonce,
                deviceId = _syncEngine.DeviceId,
                hmacProof = proof
            };

            var verifyResponse = await client.PostAsJsonAsync(
                $"{httpBaseUrl}/rest/cluster/key/verify", verifyPayload);

            return verifyResponse.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Challenge-response failed for device {DeviceId} at {Url}",
                remoteDeviceId, httpBaseUrl);
            return false;
        }
    }

    /// <summary>
    /// Resolve an HTTP base URL from discovered addresses.
    /// Discovered addresses are typically tcp://host:port or quic://host:port.
    /// We derive an HTTP URL from the host, using the remote agent's HTTP port.
    /// Since we don't know the remote port, we assume it matches our own.
    /// </summary>
    private string? ResolveHttpAddress(List<DiscoveredAddress> addresses)
    {
        foreach (var addr in addresses.OrderBy(a => a.Priority))
        {
            try
            {
                if (Uri.TryCreate(addr.Address, UriKind.Absolute, out var uri))
                {
                    return $"http://{uri.Host}:{_agentHttpPort}";
                }
            }
            catch
            {
                // Skip malformed addresses
            }
        }
        return null;
    }

    private static int ResolveAgentPort(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        // Try ClusterKey:AgentHttpPort first, then parse Urls/Kestrel config
        var portStr = configuration["ClusterKey:AgentHttpPort"];
        if (int.TryParse(portStr, out var port))
            return port;

        // Try to parse from ASPNETCORE_URLS or Urls config
        var urls = configuration["Urls"] ?? configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrEmpty(urls))
        {
            foreach (var url in urls.Split(';'))
            {
                if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
                    uri.Scheme == "http")
                    return uri.Port;
            }
        }

        return 5000;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _discoveryManager.DeviceDiscovered -= OnDeviceDiscovered;
            _disposed = true;
        }
    }

    private class ChallengeResponseDto
    {
        public string Nonce { get; set; } = "";
        public string DeviceId { get; set; } = "";
    }
}
