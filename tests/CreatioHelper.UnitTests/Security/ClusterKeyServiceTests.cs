using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CreatioHelper.UnitTests.Security;

public class ClusterKeyServiceTests
{
    private static ClusterKeyService CreateService(
        string key = "test-cluster-key",
        bool enabled = true,
        int timeoutSeconds = 30,
        int maxPerMinute = 10,
        string localDeviceId = "local-device-001")
    {
        var config = new ClusterKeyConfiguration
        {
            Enabled = enabled,
            Key = key,
            ChallengeTimeoutSeconds = timeoutSeconds,
            MaxChallengesPerMinute = maxPerMinute
        };

        var logger = new Mock<ILogger<ClusterKeyService>>();
        var options = Options.Create(config);
        var syncEngine = new Mock<ISyncEngine>();
        syncEngine.Setup(x => x.DeviceId).Returns(localDeviceId);

        return new ClusterKeyService(logger.Object, options, syncEngine.Object);
    }

    [Fact]
    public void IsEnabled_ReturnsTrueWhenConfigured()
    {
        var service = CreateService();
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void IsEnabled_ReturnsFalseWhenDisabled()
    {
        var service = CreateService(enabled: false);
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void IsEnabled_ReturnsFalseWhenKeyEmpty()
    {
        var service = CreateService(key: "");
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void GenerateChallenge_ReturnsNonce()
    {
        var service = CreateService();
        var challenge = service.GenerateChallenge("remote-device-001");

        Assert.NotNull(challenge);
        Assert.False(string.IsNullOrEmpty(challenge.Nonce));
        Assert.Equal("local-device-001", challenge.DeviceId);
    }

    [Fact]
    public void GenerateChallenge_ReturnsNullWhenDisabled()
    {
        var service = CreateService(enabled: false);
        var challenge = service.GenerateChallenge("remote-device-001");

        Assert.Null(challenge);
    }

    [Fact]
    public void MatchingKeys_VerificationSucceeds()
    {
        const string clusterKey = "shared-secret-key";
        const string localDeviceId = "device-A";
        const string remoteDeviceId = "device-B";

        // Agent B (local) generates challenge
        var serviceB = CreateService(key: clusterKey, localDeviceId: localDeviceId);
        var challenge = serviceB.GenerateChallenge(remoteDeviceId);
        Assert.NotNull(challenge);

        // Agent A (remote) computes proof with same key
        var serviceA = CreateService(key: clusterKey, localDeviceId: remoteDeviceId);
        var proof = serviceA.ComputeProof(challenge.Nonce, remoteDeviceId, challenge.DeviceId);

        // Agent B verifies
        var result = serviceB.VerifyChallenge(challenge.Nonce, remoteDeviceId, proof);
        Assert.True(result);
    }

    [Fact]
    public void DifferentKeys_VerificationFails()
    {
        const string localDeviceId = "device-A";
        const string remoteDeviceId = "device-B";

        // Agent B generates challenge with key1
        var serviceB = CreateService(key: "key-one", localDeviceId: localDeviceId);
        var challenge = serviceB.GenerateChallenge(remoteDeviceId);
        Assert.NotNull(challenge);

        // Agent A computes proof with key2
        var serviceA = CreateService(key: "key-two", localDeviceId: remoteDeviceId);
        var proof = serviceA.ComputeProof(challenge.Nonce, remoteDeviceId, challenge.DeviceId);

        // Agent B verifies — should fail
        var result = serviceB.VerifyChallenge(challenge.Nonce, remoteDeviceId, proof);
        Assert.False(result);
    }

    [Fact]
    public void ExpiredChallenge_VerificationFails()
    {
        const string clusterKey = "shared-secret";
        const string localDeviceId = "device-A";
        const string remoteDeviceId = "device-B";

        // Use 0-second timeout so challenge expires immediately
        var serviceB = CreateService(key: clusterKey, localDeviceId: localDeviceId, timeoutSeconds: 0);
        var challenge = serviceB.GenerateChallenge(remoteDeviceId);
        Assert.NotNull(challenge);

        // Wait a tiny bit to ensure expiration
        Thread.Sleep(10);

        var serviceA = CreateService(key: clusterKey, localDeviceId: remoteDeviceId);
        var proof = serviceA.ComputeProof(challenge.Nonce, remoteDeviceId, challenge.DeviceId);

        var result = serviceB.VerifyChallenge(challenge.Nonce, remoteDeviceId, proof);
        Assert.False(result);
    }

    [Fact]
    public void ReusedNonce_VerificationFails()
    {
        const string clusterKey = "shared-secret";
        const string localDeviceId = "device-A";
        const string remoteDeviceId = "device-B";

        var serviceB = CreateService(key: clusterKey, localDeviceId: localDeviceId);
        var challenge = serviceB.GenerateChallenge(remoteDeviceId);
        Assert.NotNull(challenge);

        var serviceA = CreateService(key: clusterKey, localDeviceId: remoteDeviceId);
        var proof = serviceA.ComputeProof(challenge.Nonce, remoteDeviceId, challenge.DeviceId);

        // First verification succeeds
        Assert.True(serviceB.VerifyChallenge(challenge.Nonce, remoteDeviceId, proof));

        // Second verification with same nonce fails (one-time use)
        Assert.False(serviceB.VerifyChallenge(challenge.Nonce, remoteDeviceId, proof));
    }

    [Fact]
    public void RateLimiting_ExceedsLimit()
    {
        var service = CreateService(maxPerMinute: 3);

        // First 3 should succeed
        for (int i = 0; i < 3; i++)
        {
            var challenge = service.GenerateChallenge("spammy-device");
            Assert.NotNull(challenge);
        }

        // 4th should be rate-limited
        var blocked = service.GenerateChallenge("spammy-device");
        Assert.Null(blocked);
    }

    [Fact]
    public void RateLimiting_DifferentDevicesNotAffected()
    {
        var service = CreateService(maxPerMinute: 2);

        // Max out device-1
        service.GenerateChallenge("device-1");
        service.GenerateChallenge("device-1");
        Assert.Null(service.GenerateChallenge("device-1"));

        // device-2 should still work
        var challenge = service.GenerateChallenge("device-2");
        Assert.NotNull(challenge);
    }

    [Fact]
    public void WrongDeviceId_VerificationFails()
    {
        const string clusterKey = "shared-secret";

        var service = CreateService(key: clusterKey, localDeviceId: "device-A");
        var challenge = service.GenerateChallenge("device-B");
        Assert.NotNull(challenge);

        // Attacker tries to use challenge meant for device-B
        var proof = service.ComputeProof(challenge.Nonce, "device-C", challenge.DeviceId);
        var result = service.VerifyChallenge(challenge.Nonce, "device-C", proof);
        Assert.False(result);
    }
}
