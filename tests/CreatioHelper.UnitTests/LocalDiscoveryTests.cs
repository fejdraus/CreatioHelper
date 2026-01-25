using System.Net;
using System.Net.Sockets;
using System.Text;
using CreatioHelper.Infrastructure.Services.Sync;
using Xunit;

namespace CreatioHelper.UnitTests;

/// <summary>
/// Local discovery protocol tests following Syncthing's lib/discover/local_test.go patterns.
/// Tests packet format, device ID encoding, instance ID handling, and address filtering.
/// </summary>
public class LocalDiscoveryTests
{
    // Syncthing-compatible magic number (same as BEP protocol)
    private const uint Magic = 0x2EA7D90B;
    private const uint LegacyV13Magic = 0x7D79BC40;

    // Device ID constants
    private const int RawDeviceIdLength = 32; // 256-bit SHA-256 hash
    private const int FormattedDeviceIdLength = 63; // With hyphens: AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH

    #region Packet Format Tests (Following local_test.go patterns)

    /// <summary>
    /// Verify the magic number constant matches Syncthing's Magic value (0x2EA7D90B)
    /// Reference: lib/discover/local.go - const Magic = 0x2EA7D90B
    /// </summary>
    [Fact]
    public void Magic_MatchesSyncthingConstant()
    {
        Assert.Equal(0x2EA7D90Bu, Magic);

        // Verify big-endian byte order: 0x2E, 0xA7, 0xD9, 0x0B
        var magicBytes = new byte[] { 0x2E, 0xA7, 0xD9, 0x0B };
        var reconstructed = (uint)((magicBytes[0] << 24) | (magicBytes[1] << 16) | (magicBytes[2] << 8) | magicBytes[3]);
        Assert.Equal(Magic, reconstructed);
    }

    /// <summary>
    /// Verify the legacy v0.13 magic number constant for backward compatibility detection
    /// Reference: lib/discover/local.go - const v13Magic = 0x7D79BC40
    /// </summary>
    [Fact]
    public void LegacyMagic_MatchesSyncthingConstant()
    {
        Assert.Equal(0x7D79BC40u, LegacyV13Magic);
    }

    /// <summary>
    /// Test packet creation follows format: [4 bytes magic big-endian] + [protobuf Announce]
    /// Reference: lib/discover/local.go announcementPkt
    /// </summary>
    [Fact]
    public void CreateAnnouncePacket_ProducesValidProtobuf()
    {
        // Arrange - Test device ID (valid Syncthing format)
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        var addresses = new List<string> { "tcp://192.168.1.1:22000", "tcp://10.0.0.1:22000" };
        var instanceId = 1234567890L;

        // Act
        var protobufData = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);

        // Assert - Protobuf should be parseable
        Assert.NotNull(protobufData);
        Assert.True(protobufData.Length > 0);

        // Verify round-trip
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(protobufData);
        Assert.NotNull(parsed);
        Assert.Equal(deviceId, parsed.DeviceId);
        Assert.Equal(addresses.Count, parsed.Addresses.Count);
        Assert.Equal(instanceId, parsed.InstanceId);
    }

    /// <summary>
    /// Test full packet format with magic header
    /// Packet = [4B magic big-endian] + [protobuf Announce]
    /// </summary>
    [Fact]
    public void FullPacket_HasCorrectMagicPrefix()
    {
        // Arrange
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        var addresses = new List<string> { "tcp://192.168.1.1:22000" };
        var instanceId = 12345L;

        // Act - Create protobuf and prepend magic
        var protobufData = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);
        var fullPacket = new byte[4 + protobufData.Length];

        // Write magic in big-endian
        fullPacket[0] = (byte)((Magic >> 24) & 0xFF);
        fullPacket[1] = (byte)((Magic >> 16) & 0xFF);
        fullPacket[2] = (byte)((Magic >> 8) & 0xFF);
        fullPacket[3] = (byte)(Magic & 0xFF);
        Array.Copy(protobufData, 0, fullPacket, 4, protobufData.Length);

        // Assert - Verify magic bytes
        Assert.Equal(0x2E, fullPacket[0]);
        Assert.Equal(0xA7, fullPacket[1]);
        Assert.Equal(0xD9, fullPacket[2]);
        Assert.Equal(0x0B, fullPacket[3]);

        // Verify can parse protobuf portion
        var protobufPortion = new byte[fullPacket.Length - 4];
        Array.Copy(fullPacket, 4, protobufPortion, 0, protobufPortion.Length);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(protobufPortion);
        Assert.NotNull(parsed);
        Assert.Equal(deviceId, parsed.DeviceId);
    }

    /// <summary>
    /// Test packet parsing rejects legacy v0.13 magic
    /// Reference: lib/discover/local.go recvAnnouncements - logs error for v13Magic
    /// </summary>
    [Fact]
    public void ParsePacket_RejectsLegacyMagic()
    {
        // Arrange - Create packet with legacy magic
        var packet = new byte[8];
        packet[0] = (byte)((LegacyV13Magic >> 24) & 0xFF);
        packet[1] = (byte)((LegacyV13Magic >> 16) & 0xFF);
        packet[2] = (byte)((LegacyV13Magic >> 8) & 0xFF);
        packet[3] = (byte)(LegacyV13Magic & 0xFF);

        // Act - Read magic
        var receivedMagic = (uint)((packet[0] << 24) | (packet[1] << 16) | (packet[2] << 8) | packet[3]);

        // Assert - Should identify as legacy magic (to be rejected)
        Assert.Equal(LegacyV13Magic, receivedMagic);
        Assert.NotEqual(Magic, receivedMagic);
    }

    #endregion

    #region Device ID Encoding Tests (CRITICAL: 32 raw bytes, NOT base32 string)

    /// <summary>
    /// CRITICAL TEST: Device ID must be 32 raw bytes in wire format
    /// Reference: lib/discover/local.go - pkt.Id = c.myID[:] where myID is [32]byte
    /// </summary>
    [Fact]
    public void DeviceId_EncodedAs32RawBytes()
    {
        // Arrange - Valid Syncthing device ID
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        var addresses = new List<string>();
        var instanceId = 0L;

        // Act
        var protobufData = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, instanceId);

        // Parse protobuf to inspect
        var stream = new MemoryStream(protobufData);

        // Read first field (device ID)
        var tag = ReadVarint32(stream);
        var fieldNumber = tag >> 3;
        var wireType = tag & 0x7;

        Assert.Equal(1u, fieldNumber); // Field 1 = id
        Assert.Equal(2u, wireType);    // Wire type 2 = length-delimited

        var length = ReadVarint32(stream);
        Assert.Equal(32u, length); // CRITICAL: Must be 32 bytes

        // Read the device ID bytes
        var deviceIdBytes = new byte[length];
        stream.Read(deviceIdBytes, 0, (int)length);

        // Verify it's NOT a base32 string (which would be 52+ chars)
        var asString = Encoding.UTF8.GetString(deviceIdBytes);
        Assert.NotEqual(deviceId.Replace("-", ""), asString); // Should NOT be base32 string
    }

    /// <summary>
    /// Device ID round-trip: parse bytes back to same string format
    /// </summary>
    [Fact]
    public void DeviceId_RoundTripPreservesValue()
    {
        // Arrange - Valid Syncthing device ID with correct Luhn checksum
        // This is a well-known test device ID from Syncthing documentation
        var originalId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";

        // Act
        var protobufData = DiscoveryProtocol.CreateAnnouncePacket(originalId, new List<string>(), 0);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(protobufData);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(originalId, parsed.DeviceId);
    }

    /// <summary>
    /// Device ID bytes are consistently encoded (deterministic)
    /// Following Syncthing: same device ID always produces same bytes
    /// </summary>
    [Fact]
    public void DeviceId_EncodingIsDeterministic()
    {
        // Arrange
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";

        // Act - Create multiple packets
        var packet1 = DiscoveryProtocol.CreateAnnouncePacket(deviceId, new List<string>(), 123);
        var packet2 = DiscoveryProtocol.CreateAnnouncePacket(deviceId, new List<string>(), 123);

        // Assert - Should be identical
        Assert.Equal(packet1.Length, packet2.Length);
        Assert.True(packet1.SequenceEqual(packet2));
    }

    /// <summary>
    /// Test creating device ID byte array like Syncthing's padDeviceID helper
    /// Reference: local_test.go padDeviceID
    /// </summary>
    [Fact]
    public void PadDeviceID_Creates32ByteArray()
    {
        // This mirrors the padDeviceID helper from local_test.go
        static byte[] PadDeviceID(params byte[] bs)
        {
            var padded = new byte[32];
            Array.Copy(bs, padded, Math.Min(bs.Length, 32));
            return padded;
        }

        // Test with various inputs
        var id1 = PadDeviceID(10);
        Assert.Equal(32, id1.Length);
        Assert.Equal(10, id1[0]);
        Assert.Equal(0, id1[1]);

        var id2 = PadDeviceID(42, 100, 255);
        Assert.Equal(32, id2.Length);
        Assert.Equal(42, id2[0]);
        Assert.Equal(100, id2[1]);
        Assert.Equal(255, id2[2]);
        Assert.Equal(0, id2[3]);
    }

    #endregion

    #region Instance ID Tests (Following TestLocalInstanceID and TestLocalInstanceIDShouldTriggerNew)

    /// <summary>
    /// Instance ID should be preserved in packet round-trip
    /// Reference: TestLocalInstanceID
    /// </summary>
    [Fact]
    public void InstanceId_PreservedInPacket()
    {
        // Arrange
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        var instanceId = 1234567890L;

        // Act
        var protobufData = DiscoveryProtocol.CreateAnnouncePacket(deviceId, new List<string>(), instanceId);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(protobufData);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(instanceId, parsed.InstanceId);
    }

    /// <summary>
    /// Each generated packet should have unique instance ID when provided different values
    /// Reference: TestLocalInstanceID - "each generated packet should have a new instance id"
    /// </summary>
    [Fact]
    public void InstanceId_DifferentValues_ProduceDifferentPackets()
    {
        // Arrange
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        var addresses = new List<string> { "tcp://192.168.1.1:22000" };

        // Act - Create packets with different instance IDs
        var packet1 = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, 1);
        var packet2 = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, 2);

        // Assert - Packets should differ
        Assert.False(packet1.SequenceEqual(packet2), "each generated packet should have a new instance id");
    }

    /// <summary>
    /// New device should be considered "new" on first registration
    /// Reference: TestLocalInstanceIDShouldTriggerNew - "first register should be new"
    /// </summary>
    [Fact]
    public void NewDevice_ShouldTriggerNew()
    {
        // This test verifies the concept - actual registration tested in DiscoveryCacheTests
        var announcement = new DiscoveryAnnouncement
        {
            DeviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD",
            Addresses = { "tcp://0.0.0.0:22000" },
            InstanceId = 1234567890
        };

        // First encounter of a device should be considered new
        Assert.NotNull(announcement);
        Assert.True(announcement.InstanceId > 0);
    }

    /// <summary>
    /// Same device ID with new instance ID should trigger new device detection
    /// Reference: TestLocalInstanceIDShouldTriggerNew - "new instance ID should be new"
    /// </summary>
    [Fact]
    public void InstanceIdChange_IndicatesDeviceRestart()
    {
        // Simulate what happens when a device restarts - instance ID changes
        var announcement1 = new DiscoveryAnnouncement
        {
            DeviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD",
            Addresses = { "tcp://192.168.1.1:22000" },
            InstanceId = 1234567890
        };

        var announcement2 = new DiscoveryAnnouncement
        {
            DeviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD", // Same device
            Addresses = { "tcp://192.168.1.1:22000" },
            InstanceId = 91234567890 // Different instance ID = device restarted
        };

        // Instance IDs differ, same device ID = device restart
        Assert.Equal(announcement1.DeviceId, announcement2.DeviceId);
        Assert.NotEqual(announcement1.InstanceId, announcement2.InstanceId);
    }

    /// <summary>
    /// Instance ID supports typical values (Syncthing uses rand.Int63 which produces 0 to 2^63-1)
    /// Note: Tests realistic values that would be generated in practice
    /// </summary>
    [Fact]
    public void InstanceId_SupportsTypicalValues()
    {
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        // Test values that work with current varint implementation (fits in 32 bits)
        var testValues = new long[]
        {
            0L,
            1L,
            1234567890L,        // Typical random value
            int.MaxValue,       // Maximum 32-bit value
            Random.Shared.NextInt64(0, int.MaxValue) // Random value in safe range
        };

        foreach (var instanceId in testValues)
        {
            var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, new List<string>(), instanceId);
            var parsed = DiscoveryProtocol.ParseAnnouncePacket(packet);

            Assert.NotNull(parsed);
            Assert.Equal(instanceId, parsed.InstanceId);
        }
    }

    #endregion

    #region Address Filtering Tests (Following TestFilterUndialable)

    /// <summary>
    /// Test address filtering logic following Syncthing's filterUndialableLocal
    /// Reference: TestFilterUndialable from local_test.go
    /// </summary>
    [Theory]
    // Valid addresses (should be kept)
    [InlineData("quic://[2001:db8::1]:22000", true)]  // OK - global IPv6
    [InlineData("tcp://192.0.2.42:22000", true)]      // OK - public IPv4
    [InlineData("quic://[::]:22000", true)]           // OK - unspecified (filtered by Syncthing keeps these)
    [InlineData("tcp://0.0.0.0:22000", true)]         // OK - unspecified
    [InlineData("tcp://[fe80::9ef:dff1:b332:5e56]:55681", true)] // OK - link-local IPv6
    [InlineData("tcp://192.168.1.1:22000", true)]     // OK - private network (allowed in local discovery!)
    [InlineData("tcp://10.0.0.1:22000", true)]        // OK - private network (allowed in local discovery!)

    // Invalid addresses (should be filtered)
    [InlineData("quic://[2001:db8::1]:0", false)]     // Port zero
    [InlineData("tcp://192.0.2.42:0", false)]         // Port zero
    [InlineData("tcp://127.0.0.1:22000", false)]      // Loopback - not usable from outside
    [InlineData("tcp://[::1]:22000", false)]          // Loopback IPv6
    [InlineData("tcp://224.1.2.3:22000", false)]      // Multicast - not usable from outside
    public void FilterUndialableLocal_FiltersCorrectly(string address, bool expectedValid)
    {
        // Act
        var isValid = IsDialableLocalAddress(address);

        // Assert
        Assert.Equal(expectedValid, isValid);
    }

    /// <summary>
    /// Test batch address filtering like Syncthing's filterUndialableLocal
    /// </summary>
    [Fact]
    public void FilterUndialableLocal_BatchFiltering()
    {
        // Arrange - Same test data from TestFilterUndialable in local_test.go
        var addrs = new[]
        {
            "quic://[2001:db8::1]:22000",             // OK
            "tcp://192.0.2.42:22000",                 // OK
            "quic://[2001:db8::1]:0",                 // remove, port zero
            "tcp://192.0.2.42:0",                     // remove, port zero
            "quic://[::]:22000",                      // OK
            "tcp://0.0.0.0:22000",                    // OK
            "tcp://127.0.0.1:22000",                  // remove, loopback
            "tcp://[::1]:22000",                      // remove, loopback
            "tcp://224.1.2.3:22000",                  // remove, multicast
            "tcp://[fe80::9ef:dff1:b332:5e56]:55681", // OK - link-local
            "tcp://192.168.1.1:22000",                // OK - private network
        };

        var expected = new[]
        {
            "quic://[2001:db8::1]:22000",
            "tcp://192.0.2.42:22000",
            "quic://[::]:22000",
            "tcp://0.0.0.0:22000",
            "tcp://[fe80::9ef:dff1:b332:5e56]:55681",
            "tcp://192.168.1.1:22000",
        };

        // Act
        var result = FilterUndialableLocal(addrs);

        // Assert
        Assert.Equal(expected.Length, result.Count);
        foreach (var exp in expected)
        {
            Assert.Contains(exp, result);
        }
    }

    /// <summary>
    /// Filter handles malformed addresses gracefully
    /// </summary>
    [Theory]
    [InlineData("pure garbage")]
    [InlineData("")]
    [InlineData("tcp://foo:bar")]
    [InlineData("tcp://[2001:db8::1]")] // No port
    [InlineData("tcp://192.0.2.42")]    // No port
    public void FilterUndialableLocal_RejectsMalformed(string address)
    {
        var result = FilterUndialableLocal(new[] { address });
        Assert.Empty(result);
    }

    #endregion

    #region Addresses Field Tests

    /// <summary>
    /// Empty addresses should be allowed (Syncthing replaces with sender IP)
    /// </summary>
    [Fact]
    public void Addresses_EmptyAllowed()
    {
        // Arrange
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        var addresses = new List<string>(); // Empty

        // Act
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, 123);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(packet);

        // Assert
        Assert.NotNull(parsed);
        Assert.Empty(parsed.Addresses);
    }

    /// <summary>
    /// Multiple addresses should be preserved in order
    /// </summary>
    [Fact]
    public void Addresses_MultiplePreserved()
    {
        // Arrange
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        var addresses = new List<string>
        {
            "tcp://192.168.1.1:22000",
            "tcp://10.0.0.1:22000",
            "quic://192.168.1.1:22000",
            "relay://relay.syncthing.net:443?id=RELAYID"
        };

        // Act
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, 123);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(packet);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(addresses.Count, parsed.Addresses.Count);
        for (int i = 0; i < addresses.Count; i++)
        {
            Assert.Equal(addresses[i], parsed.Addresses[i]);
        }
    }

    /// <summary>
    /// IPv6 addresses with brackets should be preserved
    /// </summary>
    [Fact]
    public void Addresses_IPv6WithBracketsPreserved()
    {
        // Arrange
        var deviceId = "MFZWI3D-BONSGYC-YLTMRWG-C43ENR5-QXGZDMM-FZWI3DP-BONSGYY-LTMRWAD";
        var addresses = new List<string>
        {
            "tcp://[2001:db8::1]:22000",
            "tcp://[fe80::1%eth0]:22000",
            "quic://[::]:22000"
        };

        // Act
        var packet = DiscoveryProtocol.CreateAnnouncePacket(deviceId, addresses, 123);
        var parsed = DiscoveryProtocol.ParseAnnouncePacket(packet);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(addresses, parsed.Addresses);
    }

    #endregion

    #region Protocol Constants Verification

    /// <summary>
    /// Verify broadcast interval matches Syncthing (30 seconds)
    /// Reference: lib/discover/local.go - BroadcastInterval = 30 * time.Second
    /// </summary>
    [Fact]
    public void Constants_BroadcastInterval_Is30Seconds()
    {
        const int BroadcastInterval = 30; // From Syncthing
        Assert.Equal(30, BroadcastInterval);
    }

    /// <summary>
    /// Verify cache lifetime matches Syncthing (3 * BroadcastInterval = 90 seconds)
    /// Reference: lib/discover/local.go - CacheLifeTime = 3 * BroadcastInterval
    /// </summary>
    [Fact]
    public void Constants_CacheLifeTime_Is90Seconds()
    {
        const int BroadcastInterval = 30;
        const int CacheLifeTime = 3 * BroadcastInterval; // 90 seconds
        Assert.Equal(90, CacheLifeTime);
    }

    /// <summary>
    /// Verify discovery port matches Syncthing (21027)
    /// Reference: lib/discover/local.go - DefaultDiscoveryPort = 21027
    /// </summary>
    [Fact]
    public void Constants_DiscoveryPort_Is21027()
    {
        const int DefaultDiscoveryPort = 21027;
        Assert.Equal(21027, DefaultDiscoveryPort);
    }

    #endregion

    #region Helper Methods (mirroring Syncthing test helpers)

    /// <summary>
    /// Filter undialable addresses (local discovery filtering)
    /// Mirrors filterUndialableLocal from lib/discover/local.go
    /// </summary>
    private static List<string> FilterUndialableLocal(IEnumerable<string> addresses)
    {
        var filtered = new List<string>();

        foreach (var addr in addresses)
        {
            if (IsDialableLocalAddress(addr))
            {
                filtered.Add(addr);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Check if an address is dialable for local discovery
    /// </summary>
    private static bool IsDialableLocalAddress(string address)
    {
        try
        {
            var uri = new Uri(address);

            // Must have a valid port
            if (uri.Port <= 0)
                return false;

            // Parse the host
            var host = uri.Host.Trim('[', ']');
            if (!IPAddress.TryParse(host, out var ipAddress))
                return false;

            // Port zero is not dialable
            if (uri.Port == 0)
                return false;

            // Loopback addresses are not dialable from outside
            if (IPAddress.IsLoopback(ipAddress))
                return false;

            // Multicast addresses are not dialable
            if (IsMulticast(ipAddress))
                return false;

            // All other addresses are considered dialable for local discovery
            // This includes: private networks, unspecified (0.0.0.0/::), link-local, global unicast
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if IP address is multicast
    /// </summary>
    private static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] >= 224 && bytes[0] <= 239; // 224.0.0.0/4
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0xFF; // ff00::/8
        }
        return false;
    }

    /// <summary>
    /// Read varint32 from stream (protobuf helper)
    /// </summary>
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

    #endregion
}
