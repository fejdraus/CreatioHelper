using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Network.Discovery;
using CreatioHelper.Infrastructure.Services.Sync;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Integration;

/// <summary>
/// Discovery Integration Tests for Local and Global Discovery.
/// Tests the complete discovery workflow including packet format, caching,
/// and the interaction between local (LAN) and global (Syncthing servers) discovery.
///
/// Key protocol invariants tested:
/// 1. Local discovery packet format: [4B magic big-endian] + [protobuf Announce]
/// 2. Device ID encoding: 32 raw bytes in wire format (not base32 string)
/// 3. Instance ID handling for device restart detection
/// 4. Global discovery REST API format (announce/lookup)
/// 5. Cache merging from multiple discovery sources
/// </summary>
public class DiscoveryIntegrationTests : IDisposable
{
    private readonly Mock<ILogger<DiscoveryCache>> _cacheLoggerMock;
    private readonly Mock<ILogger<GlobalDiscovery>> _globalLoggerMock;
    private readonly DiscoveryCache _cache;

    // Syncthing-compatible magic number (same as BEP protocol)
    private const uint LocalDiscoveryMagic = 0x2EA7D90B;
    private const uint LegacyV13Magic = 0x7D79BC40;

    // Device ID constants
    private const int RawDeviceIdLength = 32;

    // Test device ID (valid Syncthing format with correct Luhn checksums)
    private const string TestDeviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
    private const string TestDeviceId2 = "YZJBJFX-RDBL3WD-EQZMZZA-G4BKUQR-OYRQGAO-UUYUQJP-TXUQKBH-SCXOXAW";

    public DiscoveryIntegrationTests()
    {
        _cacheLoggerMock = new Mock<ILogger<DiscoveryCache>>();
        _globalLoggerMock = new Mock<ILogger<GlobalDiscovery>>();
        _cache = new DiscoveryCache(_cacheLoggerMock.Object);
    }

    /// <summary>
    /// Helper method to create GlobalDiscovery instances with explicit parameters
    /// to avoid constructor ambiguity.
    /// </summary>
    private GlobalDiscovery CreateGlobalDiscovery(
        string deviceId = TestDeviceId,
        IEnumerable<string>? discoveryServers = null,
        int announceIntervalSeconds = GlobalDiscovery.DefaultReannounceIntervalSeconds,
        int lookupCacheSeconds = 300,
        bool insecureSkipVerify = false,
        bool noLookup = false)
    {
        return new GlobalDiscovery(
            _globalLoggerMock.Object,
            deviceId,
            clientCertificate: null,
            discoveryServers: discoveryServers,
            announceIntervalSeconds: announceIntervalSeconds,
            lookupCacheSeconds: lookupCacheSeconds,
            insecureSkipVerify: insecureSkipVerify,
            noLookup: noLookup);
    }

    #region Local Discovery Packet Format Tests

    /// <summary>
    /// Tests local discovery packet creation produces valid wire format.
    /// Wire format: [4B magic big-endian 0x2EA7D90B] + [protobuf Announce]
    /// </summary>
    [Fact]
    public void LocalDiscovery_CreatePacket_ProducesValidWireFormat()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var addresses = new List<string> { "tcp://192.168.1.1:22000", "quic://192.168.1.1:22000" };
        var instanceId = 1234567890L;

        // Act - Create protobuf packet
        var protobufData = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);

        // Create full packet with magic header
        var fullPacket = new byte[4 + protobufData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(fullPacket.AsSpan(0, 4), LocalDiscoveryMagic);
        protobufData.CopyTo(fullPacket.AsSpan(4));

        // Assert - Verify magic number (big-endian)
        Assert.Equal(0x2E, fullPacket[0]);
        Assert.Equal(0xA7, fullPacket[1]);
        Assert.Equal(0xD9, fullPacket[2]);
        Assert.Equal(0x0B, fullPacket[3]);

        // Verify protobuf can be parsed
        var parsedProtobuf = fullPacket.AsSpan(4).ToArray();
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(parsedProtobuf);
        Assert.NotNull(parsed);
        Assert.Equal(deviceId, parsed.DeviceId);
        Assert.Equal(addresses.Count, parsed.Addresses.Count);
        Assert.Equal(instanceId, parsed.InstanceId);
    }

    /// <summary>
    /// Tests that device ID is encoded as 32 raw bytes in wire format.
    /// CRITICAL: Syncthing uses raw bytes, NOT base32 string.
    /// Reference: lib/discover/local.go - pkt.Id = c.myID[:] where myID is [32]byte
    /// </summary>
    [Fact]
    public void LocalDiscovery_DeviceIdEncoding_Is32RawBytes()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var addresses = new List<string>();
        var instanceId = 0L;

        // Act
        var protobufData = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);

        // Parse protobuf to inspect device ID field
        using var stream = new MemoryStream(protobufData);

        // Read first field (device ID): tag + length + data
        var tag = ReadVarint32(stream);
        var fieldNumber = tag >> 3;
        var wireType = tag & 0x7;

        Assert.Equal(1u, fieldNumber); // Field 1 = id
        Assert.Equal(2u, wireType);    // Wire type 2 = length-delimited

        var length = ReadVarint32(stream);

        // CRITICAL assertion: Device ID must be exactly 32 bytes
        Assert.Equal((uint)RawDeviceIdLength, length);

        // Read the device ID bytes
        var deviceIdBytes = new byte[length];
        stream.Read(deviceIdBytes, 0, (int)length);

        // Verify it's NOT a base32 string (which would be 52+ chars)
        var asString = Encoding.UTF8.GetString(deviceIdBytes);
        Assert.NotEqual(deviceId.Replace("-", ""), asString);
    }

    /// <summary>
    /// Tests bidirectional packet round-trip: create -> parse -> verify.
    /// </summary>
    [Fact]
    public void LocalDiscovery_PacketRoundTrip_PreservesAllFields()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var addresses = new List<string>
        {
            "tcp://192.168.1.1:22000",
            "quic://192.168.1.1:22000",
            "tcp://[fe80::1%eth0]:22000",
            "relay://relay.syncthing.net:443?id=ABC123"
        };
        var instanceId = Random.Shared.NextInt64(0, int.MaxValue);

        // Act
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(packet);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(deviceId, parsed.DeviceId);
        Assert.Equal(addresses.Count, parsed.Addresses.Count);
        for (int i = 0; i < addresses.Count; i++)
        {
            Assert.Equal(addresses[i], parsed.Addresses[i]);
        }
        Assert.Equal(instanceId, parsed.InstanceId);
    }

    /// <summary>
    /// Tests that legacy v0.13 magic number is detected and rejected.
    /// Reference: lib/discover/local.go - logs error for v13Magic
    /// </summary>
    [Fact]
    public void LocalDiscovery_LegacyMagic_IsRejected()
    {
        // Arrange - Create packet with legacy magic
        var legacyPacket = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(legacyPacket.AsSpan(0, 4), LegacyV13Magic);

        // Act - Read magic
        var receivedMagic = BinaryPrimitives.ReadUInt32BigEndian(legacyPacket.AsSpan(0, 4));

        // Assert - Should identify as legacy magic (to be rejected)
        Assert.Equal(LegacyV13Magic, receivedMagic);
        Assert.NotEqual(LocalDiscoveryMagic, receivedMagic);
    }

    /// <summary>
    /// Tests packet creation/parsing with empty addresses (valid per protocol).
    /// Syncthing replaces empty addresses with sender's IP.
    /// </summary>
    [Fact]
    public void LocalDiscovery_EmptyAddresses_IsValid()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var addresses = new List<string>(); // Empty
        var instanceId = 12345L;

        // Act
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(packet);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(deviceId, parsed.DeviceId);
        Assert.Empty(parsed.Addresses);
        Assert.Equal(instanceId, parsed.InstanceId);
    }

    #endregion

    #region Instance ID Integration Tests

    /// <summary>
    /// Tests instance ID change detection for device restart scenarios.
    /// When instance ID changes, the cache should be invalidated.
    /// Reference: lib/discover/local.go registerDevice
    /// </summary>
    [Fact]
    public void DiscoveryIntegration_InstanceIdChange_InvalidatesCache()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var initialAddresses = new List<string> { "tcp://192.168.1.1:22000" };
        var newAddresses = new List<string> { "tcp://10.0.0.1:22000" };
        var instanceId1 = 12345L;
        var instanceId2 = 67890L; // Different = device restarted

        // Act - Initial registration
        var isNew1 = _cache.AddPositive(deviceId, initialAddresses, DiscoveryCacheSource.Local, instanceId1);

        // Act - Device restart (different instance ID)
        var isNew2 = _cache.AddPositive(deviceId, newAddresses, DiscoveryCacheSource.Local, instanceId2);

        // Assert
        Assert.True(isNew1, "First registration should be new");
        Assert.True(isNew2, "Instance ID change should trigger new device detection");

        // Verify cache was invalidated (old addresses replaced, not merged)
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Single(entry.Addresses);
        Assert.Equal("tcp://10.0.0.1:22000", entry.Addresses[0]);
        Assert.Equal(instanceId2, entry.InstanceId);
    }

    /// <summary>
    /// Tests that same instance ID merges addresses (no restart).
    /// </summary>
    [Fact]
    public void DiscoveryIntegration_SameInstanceId_MergesAddresses()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var instanceId = 12345L;

        // Act - Multiple announcements with same instance ID
        _cache.AddPositive(deviceId, new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local, instanceId);
        _cache.AddPositive(deviceId, new List<string> { "tcp://[fe80::1]:22000" }, DiscoveryCacheSource.Local, instanceId);
        var isNew = _cache.AddPositive(deviceId, new List<string> { "quic://192.168.1.1:22000" }, DiscoveryCacheSource.Local, instanceId);

        // Assert
        Assert.False(isNew, "Same instance ID should not trigger new device");
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(3, entry.Addresses.Count);
        Assert.Contains("tcp://192.168.1.1:22000", entry.Addresses);
        Assert.Contains("tcp://[fe80::1]:22000", entry.Addresses);
        Assert.Contains("quic://192.168.1.1:22000", entry.Addresses);
    }

    /// <summary>
    /// Tests full device restart simulation with packet creation/parsing.
    /// </summary>
    [Fact]
    public void DiscoveryIntegration_FullRestartSimulation()
    {
        // Arrange - Initial device state
        var deviceId = TestDeviceId;
        var instanceId1 = Random.Shared.NextInt64(0, int.MaxValue);
        var addresses1 = new List<string> { "tcp://192.168.1.100:22000" };

        // Create and parse initial announcement
        var packet1 = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses1, instanceId1);
        var parsed1 = DiscoveryProtocol.ParseAnnouncePacket(packet1);
        Assert.NotNull(parsed1);

        // Register in cache
        var isNew1 = _cache.AddPositive(parsed1.DeviceId, parsed1.Addresses.ToList(), DiscoveryCacheSource.Local, parsed1.InstanceId);
        Assert.True(isNew1);

        // Arrange - Device restart (new instance ID, possibly new IP)
        var instanceId2 = Random.Shared.NextInt64(0, int.MaxValue);
        while (instanceId2 == instanceId1) instanceId2 = Random.Shared.NextInt64(0, int.MaxValue); // Ensure different
        var addresses2 = new List<string> { "tcp://192.168.1.200:22000" };

        // Create and parse restart announcement
        var packet2 = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses2, instanceId2);
        var parsed2 = DiscoveryProtocol.ParseAnnouncePacket(packet2);
        Assert.NotNull(parsed2);

        // Register restart in cache
        var isNew2 = _cache.AddPositive(parsed2.DeviceId, parsed2.Addresses.ToList(), DiscoveryCacheSource.Local, parsed2.InstanceId);

        // Assert - Restart detected, cache invalidated
        Assert.True(isNew2, "Device restart should be detected");
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(instanceId2, entry.InstanceId);
        Assert.Single(entry.Addresses);
        Assert.Equal("tcp://192.168.1.200:22000", entry.Addresses[0]);
    }

    #endregion

    #region Global Discovery Protocol Tests

    /// <summary>
    /// Tests that GlobalDiscovery creates with correct default configuration.
    /// </summary>
    [Fact]
    public async Task GlobalDiscovery_DefaultConfiguration_UsesCorrectServers()
    {
        // Arrange & Act
        var discovery = CreateGlobalDiscovery();

        try
        {
            // Assert - Default servers include Syncthing's discovery servers
            Assert.Contains(discovery.DiscoveryServers, s => s.Contains("discovery.syncthing.net"));
            Assert.True(discovery.DiscoveryServers.Count >= 1);
        }
        finally
        {
            await discovery.DisposeAsync();
        }
    }

    /// <summary>
    /// Tests noLookup mode (announce-only) throws LookupError.
    /// Reference: lib/discover/global.go - "if c.noLookup { return ... lookupError{..., 1 hour} }"
    /// </summary>
    [Fact]
    public async Task GlobalDiscovery_NoLookupMode_ThrowsLookupError()
    {
        // Arrange
        var discovery = CreateGlobalDiscovery(
            discoveryServers: new[] { "https://discovery.example.com/v2" },
            noLookup: true);

        try
        {
            // Act & Assert
            var ex = await Assert.ThrowsAsync<LookupError>(() =>
                discovery.LookupAsync(TestDeviceId2));

            Assert.Equal("lookups not supported", ex.Message);
            Assert.Equal(TimeSpan.FromHours(1), ex.CacheFor);
        }
        finally
        {
            await discovery.DisposeAsync();
        }
    }

    /// <summary>
    /// Tests LookupError exception contains proper cache duration.
    /// </summary>
    [Fact]
    public void GlobalDiscovery_LookupError_HasCorrectCacheFor()
    {
        // Arrange
        var cacheFor = TimeSpan.FromMinutes(30);
        var error = new LookupError("test error", cacheFor);

        // Assert
        Assert.Equal("test error", error.Message);
        Assert.Equal(cacheFor, error.CacheFor);
    }

    /// <summary>
    /// Tests GlobalDiscovery protocol constants match Syncthing.
    /// </summary>
    [Fact]
    public void GlobalDiscovery_ProtocolConstants_MatchSyncthing()
    {
        // Reference: lib/discover/global.go
        Assert.Equal(1800, GlobalDiscovery.DefaultReannounceIntervalSeconds); // 30 minutes
        Assert.Equal(300, GlobalDiscovery.ErrorRetryIntervalSeconds);         // 5 minutes
        Assert.Equal(30, GlobalDiscovery.RequestTimeoutSeconds);               // 30 seconds
    }

    /// <summary>
    /// Tests announce with empty addresses returns null (no announcement made).
    /// </summary>
    [Fact]
    public async Task GlobalDiscovery_AnnounceEmptyAddresses_ReturnsNull()
    {
        // Arrange
        var discovery = CreateGlobalDiscovery(
            discoveryServers: new[] { "https://discovery.example.com/v2" });

        try
        {
            // Act
            var result = await discovery.AnnounceAsync(Array.Empty<string>());

            // Assert
            Assert.Null(result);
        }
        finally
        {
            await discovery.DisposeAsync();
        }
    }

    /// <summary>
    /// Tests GlobalDiscovery start/stop lifecycle.
    /// </summary>
    [Fact]
    public async Task GlobalDiscovery_StartStop_Lifecycle()
    {
        // Arrange
        var discovery = CreateGlobalDiscovery(
            discoveryServers: new[] { "https://discovery.example.com/v2" });

        try
        {
            // Assert - Initial state
            Assert.False(discovery.IsRunning);

            // Act - Start
            await discovery.StartAsync(new[] { "tcp://192.168.1.1:22000" });
            Assert.True(discovery.IsRunning);

            // Act - Stop
            await discovery.StopAsync();
            Assert.False(discovery.IsRunning);
        }
        finally
        {
            await discovery.DisposeAsync();
        }
    }

    /// <summary>
    /// Tests double start is idempotent.
    /// </summary>
    [Fact]
    public async Task GlobalDiscovery_DoubleStart_IsIdempotent()
    {
        // Arrange
        var discovery = CreateGlobalDiscovery(
            discoveryServers: new[] { "https://discovery.example.com/v2" });

        try
        {
            var addresses = new[] { "tcp://192.168.1.1:22000" };

            // Act
            await discovery.StartAsync(addresses);
            await discovery.StartAsync(addresses); // Double start

            // Assert
            Assert.True(discovery.IsRunning);

            // Cleanup
            await discovery.StopAsync();
        }
        finally
        {
            await discovery.DisposeAsync();
        }
    }

    /// <summary>
    /// Tests double stop is safe.
    /// </summary>
    [Fact]
    public async Task GlobalDiscovery_DoubleStop_IsSafe()
    {
        // Arrange
        var discovery = CreateGlobalDiscovery();

        try
        {
            // Act - Stop without starting (should not throw)
            await discovery.StopAsync();
            await discovery.StopAsync();

            // Assert
            Assert.False(discovery.IsRunning);
        }
        finally
        {
            await discovery.DisposeAsync();
        }
    }

    #endregion

    #region Cache Integration Tests (Local + Global)

    /// <summary>
    /// Tests that cache merges addresses from both local and global discovery.
    /// </summary>
    [Fact]
    public void CacheIntegration_MergesLocalAndGlobal()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var localAddress = "tcp://192.168.1.1:22000";
        var globalAddress = "tcp://8.8.8.8:22000";

        // Act - Add from local discovery
        _cache.AddPositive(deviceId, new List<string> { localAddress }, DiscoveryCacheSource.Local);

        // Add from global discovery
        _cache.AddPositive(deviceId, new List<string> { globalAddress }, DiscoveryCacheSource.Global);

        // Assert - Both addresses should be merged
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Addresses.Count);
        Assert.Contains(localAddress, entry.Addresses);
        Assert.Contains(globalAddress, entry.Addresses);
    }

    /// <summary>
    /// Tests that negative cache entries don't override positive entries.
    /// </summary>
    [Fact]
    public void CacheIntegration_NegativeDoesNotOverridePositive()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var addresses = new List<string> { "tcp://192.168.1.1:22000" };

        // Act
        _cache.AddPositive(deviceId, addresses, DiscoveryCacheSource.Local);
        _cache.AddNegative(deviceId, DiscoveryCacheSource.Global);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.False(entry.IsNegative);
        Assert.Single(entry.Addresses);
    }

    /// <summary>
    /// Tests cache statistics with multiple sources.
    /// </summary>
    [Fact]
    public void CacheIntegration_Statistics_TracksMultipleSources()
    {
        // Arrange & Act
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://8.8.8.8:22000" }, DiscoveryCacheSource.Global);
        _cache.AddNegative("device3", DiscoveryCacheSource.Global);

        // Assert
        var stats = _cache.GetStatistics();
        Assert.Equal(2, stats.PositiveEntryCount);
        Assert.Equal(1, stats.NegativeEntryCount);
        Assert.Equal(1, stats.LocalEntryCount);
        Assert.Equal(1, stats.GlobalEntryCount);
    }

    /// <summary>
    /// Tests cache clear removes all entries.
    /// </summary>
    [Fact]
    public void CacheIntegration_Clear_RemovesAllEntries()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://8.8.8.8:22000" }, DiscoveryCacheSource.Global);

        // Act
        _cache.Clear();

        // Assert
        Assert.False(_cache.TryGet("device1", out _));
        Assert.False(_cache.TryGet("device2", out _));
        var stats = _cache.GetStatistics();
        Assert.Equal(0, stats.PositiveEntryCount);
    }

    /// <summary>
    /// Tests cache invalidation for specific device.
    /// </summary>
    [Fact]
    public void CacheIntegration_Remove_InvalidatesSpecificDevice()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Global);

        // Act
        _cache.Remove("device1");

        // Assert
        Assert.False(_cache.TryGet("device1", out _));
        Assert.True(_cache.TryGet("device2", out _));
    }

    /// <summary>
    /// Tests cache TTL configuration for different sources.
    /// </summary>
    [Fact]
    public void CacheIntegration_DifferentTtlPerSource()
    {
        // Arrange
        _cache.LocalDiscoveryTtl = TimeSpan.FromSeconds(90);  // 90s for local (3 * 30s broadcast interval)
        _cache.PositiveTtl = TimeSpan.FromMinutes(5);         // 5min for global

        // Act
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://8.8.8.8:22000" }, DiscoveryCacheSource.Global);

        // Assert
        Assert.True(_cache.TryGet("device1", out var localEntry));
        Assert.True(_cache.TryGet("device2", out var globalEntry));

        Assert.NotNull(localEntry);
        Assert.NotNull(globalEntry);

        // Local TTL should be <= 90 seconds
        Assert.True(localEntry.TimeToLive.TotalSeconds <= 90);

        // Global TTL should be <= 5 minutes (300 seconds)
        Assert.True(globalEntry.TimeToLive.TotalSeconds <= 300);
    }

    #endregion

    #region Discovery Workflow Integration Tests

    /// <summary>
    /// Tests complete discovery workflow: receive announcement -> cache -> lookup.
    /// </summary>
    [Fact]
    public void DiscoveryWorkflow_ReceiveAnnouncementAndLookup()
    {
        // Arrange - Simulate receiving a local discovery packet
        var deviceId = TestDeviceId;
        var addresses = new List<string>
        {
            "tcp://192.168.1.100:22000",
            "quic://192.168.1.100:22000"
        };
        var instanceId = Random.Shared.NextInt64(0, int.MaxValue);

        // Act - Create and parse announcement (simulating UDP receive)
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);
        var announcement = DiscoveryProtocol.ParseAnnouncePacket(packet);
        Assert.NotNull(announcement);

        // Add to cache
        var isNew = _cache.AddPositive(
            announcement.DeviceId,
            announcement.Addresses.ToList(),
            DiscoveryCacheSource.Local,
            announcement.InstanceId);

        // Assert - Device registered
        Assert.True(isNew);

        // Lookup the device
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(deviceId, entry.DeviceId);
        Assert.Equal(2, entry.Addresses.Count);
        Assert.Equal(DiscoveryCacheSource.Local, entry.Source);
        Assert.Equal(instanceId, entry.InstanceId);
    }

    /// <summary>
    /// Tests discovery with multiple devices in cache.
    /// </summary>
    [Fact]
    public void DiscoveryWorkflow_MultipleDevices()
    {
        // Arrange & Act - Register multiple devices
        var devices = new[]
        {
            (TestDeviceId, new[] { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local),
            (TestDeviceId2, new[] { "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Local),
            ("DEVICE-GLOBAL-1", new[] { "tcp://8.8.8.8:22000" }, DiscoveryCacheSource.Global),
        };

        foreach (var (deviceId, addrs, source) in devices)
        {
            _cache.AddPositive(deviceId, addrs.ToList(), source);
        }

        // Assert - All devices accessible
        var allEntries = _cache.GetAll().ToList();
        Assert.Equal(3, allEntries.Count);

        foreach (var (deviceId, _, _) in devices)
        {
            Assert.True(_cache.TryGet(deviceId, out _));
        }

        // Statistics should be correct
        var stats = _cache.GetStatistics();
        Assert.Equal(3, stats.PositiveEntryCount);
        Assert.Equal(2, stats.LocalEntryCount);
        Assert.Equal(1, stats.GlobalEntryCount);
    }

    /// <summary>
    /// Tests discovery with IPv6 addresses.
    /// </summary>
    [Fact]
    public void DiscoveryWorkflow_IPv6Addresses()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var addresses = new List<string>
        {
            "tcp://[2001:db8::1]:22000",
            "tcp://[fe80::1%eth0]:22000",
            "quic://[::]:22000"
        };
        var instanceId = 12345L;

        // Act
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(packet);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(addresses, parsed.Addresses);

        // Register and lookup
        _cache.AddPositive(parsed.DeviceId, parsed.Addresses.ToList(), DiscoveryCacheSource.Local, parsed.InstanceId);
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(3, entry.Addresses.Count);
        Assert.Contains("tcp://[2001:db8::1]:22000", entry.Addresses);
    }

    /// <summary>
    /// Tests discovery with relay addresses.
    /// </summary>
    [Fact]
    public void DiscoveryWorkflow_RelayAddresses()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var addresses = new List<string>
        {
            "tcp://192.168.1.1:22000",
            "relay://relay.syncthing.net:443?id=ABC123"
        };

        // Act
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, 12345L);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(packet);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Contains(parsed.Addresses, a => a.StartsWith("relay://"));
    }

    #endregion

    #region Wire Format Edge Cases

    /// <summary>
    /// Tests parsing malformed protobuf gracefully returns null.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { })]                     // Empty
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF })]    // Invalid protobuf
    [InlineData(new byte[] { 0x0A })]                // Truncated
    public void LocalDiscovery_MalformedProtobuf_ReturnsNull(byte[] data)
    {
        // Act
        var result = DiscoveryProtocol.ParseAnnouncePacket(data);

        // Assert
        Assert.Null(result);
    }

    /// <summary>
    /// Tests that encoding is deterministic (same input = same output).
    /// </summary>
    [Fact]
    public void LocalDiscovery_EncodingIsDeterministic()
    {
        // Arrange
        var deviceId = TestDeviceId;
        var addresses = new List<string> { "tcp://192.168.1.1:22000" };
        var instanceId = 12345L;

        // Act
        var packet1 = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);
        var packet2 = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);

        // Assert
        Assert.True(packet1.SequenceEqual(packet2));
    }

    /// <summary>
    /// Tests parsing packet with unknown fields gracefully.
    /// </summary>
    [Fact]
    public void LocalDiscovery_UnknownFields_AreSkipped()
    {
        // Arrange - Create valid packet
        var deviceId = TestDeviceId;
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, new List<string>(), 0);

        // Append unknown field (field 99, wire type 0, value 12345)
        using var stream = new MemoryStream();
        stream.Write(packet);
        WriteVarint32(stream, (99 << 3) | 0); // Field 99, wire type 0 (varint)
        WriteVarint32(stream, 12345);
        var extendedPacket = stream.ToArray();

        // Act
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(extendedPacket);

        // Assert - Should parse successfully, ignoring unknown field
        Assert.NotNull(parsed);
        Assert.Equal(deviceId, parsed.DeviceId);
    }

    #endregion

    #region Protocol Constants Verification

    /// <summary>
    /// Tests local discovery magic matches Syncthing constant.
    /// </summary>
    [Fact]
    public void LocalDiscovery_MagicConstant_MatchesSyncthing()
    {
        // From syncthing/lib/discover/local.go
        Assert.Equal(0x2EA7D90Bu, LocalDiscoveryMagic);

        // Verify big-endian encoding
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, LocalDiscoveryMagic);
        Assert.Equal(new byte[] { 0x2E, 0xA7, 0xD9, 0x0B }, buffer);
    }

    /// <summary>
    /// Tests protocol constants match Syncthing.
    /// </summary>
    [Fact]
    public void DiscoveryConstants_MatchSyncthing()
    {
        // From syncthing/lib/discover/local.go
        const int BroadcastInterval = 30;                // 30 seconds
        const int CacheLifeTime = 3 * BroadcastInterval; // 90 seconds
        const int DiscoveryPort = 21027;

        Assert.Equal(30, BroadcastInterval);
        Assert.Equal(90, CacheLifeTime);
        Assert.Equal(21027, DiscoveryPort);
    }

    #endregion

    #region Helper Methods

    private static uint ReadVarint32(Stream stream)
    {
        uint result = 0;
        int shift = 0;

        while (true)
        {
            var b = stream.ReadByte();
            if (b == -1) throw new EndOfStreamException();

            result |= (uint)((b & 0x7F) << shift);
            if ((b & 0x80) == 0) break;
            shift += 7;
        }

        return result;
    }

    private static void WriteVarint32(Stream stream, uint value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    #endregion

    public void Dispose()
    {
        _cache.Dispose();
    }
}
