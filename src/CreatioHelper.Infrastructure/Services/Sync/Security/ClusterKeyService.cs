using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Infrastructure.Services.Sync.Security;

/// <summary>
/// HMAC challenge-response service for cluster key auto-pairing.
/// The raw key never leaves the process — only HMAC proofs are exchanged.
/// </summary>
public class ClusterKeyService : IClusterKeyService
{
    private readonly ILogger<ClusterKeyService> _logger;
    private readonly ClusterKeyConfiguration _config;
    private readonly string _localDeviceId;

    // Pending challenges: nonce -> (requestingDeviceId, expiresAt)
    private readonly ConcurrentDictionary<string, PendingChallenge> _pendingChallenges = new();

    // Rate limiting: deviceId -> list of timestamps
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _rateLimiter = new();

    public ClusterKeyService(
        ILogger<ClusterKeyService> logger,
        IOptions<ClusterKeyConfiguration> config,
        ISyncEngine syncEngine)
    {
        _logger = logger;
        _config = config.Value;
        _localDeviceId = syncEngine.DeviceId;
    }

    public bool IsEnabled => _config.Enabled && !string.IsNullOrWhiteSpace(_config.Key);

    public ChallengeResponse? GenerateChallenge(string requestingDeviceId)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug("Cluster key not enabled, rejecting challenge from {DeviceId}", requestingDeviceId);
            return null;
        }

        if (!CheckRateLimit(requestingDeviceId))
        {
            _logger.LogWarning("Rate limit exceeded for device {DeviceId}", requestingDeviceId);
            return null;
        }

        CleanupExpiredChallenges();

        var nonceBytes = RandomNumberGenerator.GetBytes(32);
        var nonce = Convert.ToBase64String(nonceBytes);

        var challenge = new PendingChallenge
        {
            RequestingDeviceId = requestingDeviceId,
            ExpiresAt = DateTime.UtcNow.AddSeconds(_config.ChallengeTimeoutSeconds)
        };

        _pendingChallenges[nonce] = challenge;

        _logger.LogDebug("Generated challenge for device {DeviceId}", requestingDeviceId);

        return new ChallengeResponse
        {
            Nonce = nonce,
            DeviceId = _localDeviceId
        };
    }

    public bool VerifyChallenge(string nonce, string remoteDeviceId, string hmacProof)
    {
        if (!IsEnabled)
            return false;

        // Remove the challenge (one-time use)
        if (!_pendingChallenges.TryRemove(nonce, out var challenge))
        {
            _logger.LogWarning("Challenge not found or already used. Device: {DeviceId}", remoteDeviceId);
            return false;
        }

        // Check expiration
        if (DateTime.UtcNow > challenge.ExpiresAt)
        {
            _logger.LogWarning("Challenge expired for device {DeviceId}", remoteDeviceId);
            return false;
        }

        // Verify device ID matches the one that requested the challenge
        if (challenge.RequestingDeviceId != remoteDeviceId)
        {
            _logger.LogWarning("Device ID mismatch: expected {Expected}, got {Actual}",
                challenge.RequestingDeviceId, remoteDeviceId);
            return false;
        }

        // Compute expected HMAC: HMAC-SHA256(clusterKey, nonce + remoteDeviceId + localDeviceId)
        var expectedProof = ComputeProof(nonce, remoteDeviceId, _localDeviceId);

        // Constant-time comparison
        var expectedBytes = Convert.FromBase64String(expectedProof);
        var actualBytes = Convert.FromBase64String(hmacProof);

        var isValid = CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);

        if (isValid)
        {
            _logger.LogInformation("Cluster key verification succeeded for device {DeviceId}", remoteDeviceId);
        }
        else
        {
            _logger.LogWarning("Cluster key verification failed for device {DeviceId}", remoteDeviceId);
        }

        return isValid;
    }

    public string ComputeProof(string nonce, string localDeviceId, string remoteDeviceId)
    {
        // HMAC-SHA256(clusterKey, nonce + localDeviceId + remoteDeviceId)
        var keyBytes = Encoding.UTF8.GetBytes(_config.Key);
        var message = Encoding.UTF8.GetBytes(nonce + localDeviceId + remoteDeviceId);

        var hmac = HMACSHA256.HashData(keyBytes, message);
        return Convert.ToBase64String(hmac);
    }

    private bool CheckRateLimit(string deviceId)
    {
        var now = DateTime.UtcNow;
        var oneMinuteAgo = now.AddMinutes(-1);

        var timestamps = _rateLimiter.GetOrAdd(deviceId, _ => new ConcurrentQueue<DateTime>());

        // Remove old entries
        while (timestamps.TryPeek(out var oldest) && oldest < oneMinuteAgo)
        {
            timestamps.TryDequeue(out _);
        }

        if (timestamps.Count >= _config.MaxChallengesPerMinute)
            return false;

        timestamps.Enqueue(now);
        return true;
    }

    private void CleanupExpiredChallenges()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _pendingChallenges)
        {
            if (now > kvp.Value.ExpiresAt)
            {
                _pendingChallenges.TryRemove(kvp.Key, out _);
            }
        }
    }

    private class PendingChallenge
    {
        public string RequestingDeviceId { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
    }
}
