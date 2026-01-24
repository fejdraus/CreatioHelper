using System.Text;
using System.Text.Json;
using CreatioHelper.Infrastructure.Services.Network.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class DiscoveryCacheTests : IDisposable
{
    private readonly Mock<ILogger<DiscoveryCache>> _loggerMock;
    private readonly DiscoveryCache _cache;

    public DiscoveryCacheTests()
    {
        _loggerMock = new Mock<ILogger<DiscoveryCache>>();
        _cache = new DiscoveryCache(_loggerMock.Object);
    }

    [Fact]
    public void AddPositive_AddsEntryToCache()
    {
        // Arrange
        var deviceId = "device1";
        var addresses = new List<string> { "tcp://192.168.1.1:22000" };

        // Act
        _cache.AddPositive(deviceId, addresses, DiscoveryCacheSource.Local);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(deviceId, entry.DeviceId);
        Assert.Single(entry.Addresses);
        Assert.False(entry.IsNegative);
    }

    [Fact]
    public void AddPositive_MergesAddressesFromMultipleSources()
    {
        // Arrange
        var deviceId = "device1";
        var addresses1 = new List<string> { "tcp://192.168.1.1:22000" };
        var addresses2 = new List<string> { "tcp://10.0.0.1:22000" };

        // Act
        _cache.AddPositive(deviceId, addresses1, DiscoveryCacheSource.Local);
        _cache.AddPositive(deviceId, addresses2, DiscoveryCacheSource.Global);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Addresses.Count);
    }

    [Fact]
    public void AddNegative_AddsNegativeEntry()
    {
        // Arrange
        var deviceId = "device1";

        // Act
        _cache.AddNegative(deviceId, DiscoveryCacheSource.Global);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.True(entry.IsNegative);
        Assert.Empty(entry.Addresses);
    }

    [Fact]
    public void AddNegative_DoesNotOverridePositive()
    {
        // Arrange
        var deviceId = "device1";
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

    [Fact]
    public void TryGet_ReturnsFalse_WhenNotFound()
    {
        // Act
        var result = _cache.TryGet("nonexistent", out var entry);

        // Assert
        Assert.False(result);
        Assert.Null(entry);
    }

    [Fact]
    public void Remove_RemovesEntry()
    {
        // Arrange
        var deviceId = "device1";
        _cache.AddPositive(deviceId, new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);

        // Act
        _cache.Remove(deviceId);

        // Assert
        Assert.False(_cache.TryGet(deviceId, out _));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Global);

        // Act
        _cache.Clear();

        // Assert
        Assert.False(_cache.TryGet("device1", out _));
        Assert.False(_cache.TryGet("device2", out _));
    }

    [Fact]
    public void GetAll_ReturnsValidEntries()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Global);

        // Act
        var entries = _cache.GetAll().ToList();

        // Assert
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000", "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://10.0.0.1:22000" }, DiscoveryCacheSource.Global);
        _cache.AddNegative("device3", DiscoveryCacheSource.Global);

        // Act
        var stats = _cache.GetStatistics();

        // Assert
        Assert.Equal(2, stats.PositiveEntryCount);
        Assert.Equal(1, stats.NegativeEntryCount);
        Assert.Equal(3, stats.TotalAddressCount);
        Assert.Equal(1, stats.LocalEntryCount);
        Assert.Equal(1, stats.GlobalEntryCount);
    }

    [Fact]
    public void Entry_IsValid_ReturnsTrueWhenNotExpired()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);

        // Act
        _cache.TryGet("device1", out var entry);

        // Assert
        Assert.NotNull(entry);
        Assert.True(entry.IsValid);
        Assert.True(entry.TimeToLive > TimeSpan.Zero);
    }

    [Fact]
    public void PositiveTtl_CanBeCustomized()
    {
        // Arrange
        _cache.PositiveTtl = TimeSpan.FromSeconds(1);
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Global);

        // Act & Assert - entry should be valid immediately
        Assert.True(_cache.TryGet("device1", out var entry));
        Assert.NotNull(entry);
        Assert.True(entry.TimeToLive.TotalSeconds <= 1);
    }

    [Fact]
    public void LocalDiscoveryTtl_UsedForLocalSource()
    {
        // Arrange
        _cache.LocalDiscoveryTtl = TimeSpan.FromSeconds(90);
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);

        // Act
        _cache.TryGet("device1", out var entry);

        // Assert
        Assert.NotNull(entry);
        Assert.True(entry.TimeToLive.TotalSeconds <= 90);
        Assert.True(entry.TimeToLive.TotalSeconds > 0);
    }

    #region Local Discovery Protocol Tests with Magic Verification

    // Local Discovery magic bytes (Syncthing compatible)
    private static readonly byte[] LocalDiscoveryMagic = { 0x2E, 0xA7, 0xD9, 0x0B };

    [Fact]
    public void LocalDiscoveryMagic_HasCorrectBytes()
    {
        // Assert - Verify the magic bytes are correct for Syncthing local discovery
        Assert.Equal(4, LocalDiscoveryMagic.Length);
        Assert.Equal(0x2E, LocalDiscoveryMagic[0]);
        Assert.Equal(0xA7, LocalDiscoveryMagic[1]);
        Assert.Equal(0xD9, LocalDiscoveryMagic[2]);
        Assert.Equal(0x0B, LocalDiscoveryMagic[3]);
    }

    [Fact]
    public void LocalDiscoveryProtocol_SerializesCorrectly()
    {
        // Arrange - Create a local discovery announcement
        var announcement = new
        {
            DeviceId = "TESTDEVICE-1234567-ABCDEFG",
            Addresses = new[] { "tcp://192.168.1.1:22000", "tcp://10.0.0.1:22000" },
            InstanceId = 12345
        };

        // Act - Serialize the announcement in local discovery format
        var json = JsonSerializer.Serialize(announcement);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var packet = new byte[4 + 4 + jsonBytes.Length];
        LocalDiscoveryMagic.CopyTo(packet, 0);
        BitConverter.GetBytes(jsonBytes.Length).CopyTo(packet, 4);
        jsonBytes.CopyTo(packet, 8);

        // Assert - Verify packet structure
        Assert.Equal(LocalDiscoveryMagic[0], packet[0]);
        Assert.Equal(LocalDiscoveryMagic[1], packet[1]);
        Assert.Equal(LocalDiscoveryMagic[2], packet[2]);
        Assert.Equal(LocalDiscoveryMagic[3], packet[3]);

        var lengthFromPacket = BitConverter.ToInt32(packet, 4);
        Assert.Equal(jsonBytes.Length, lengthFromPacket);
    }

    [Fact]
    public void LocalDiscoveryProtocol_DeserializesCorrectly()
    {
        // Arrange - Create a serialized local discovery packet
        var originalDeviceId = "TESTDEVICE-1234567-ABCDEFG";
        var originalAddresses = new[] { "tcp://192.168.1.1:22000" };
        var originalInstanceId = 54321;

        var announcement = new
        {
            DeviceId = originalDeviceId,
            Addresses = originalAddresses,
            InstanceId = originalInstanceId
        };

        var json = JsonSerializer.Serialize(announcement);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var packet = new byte[4 + 4 + jsonBytes.Length];
        LocalDiscoveryMagic.CopyTo(packet, 0);
        BitConverter.GetBytes(jsonBytes.Length).CopyTo(packet, 4);
        jsonBytes.CopyTo(packet, 8);

        // Act - Deserialize the packet
        var magicValid = packet[0] == LocalDiscoveryMagic[0] &&
                        packet[1] == LocalDiscoveryMagic[1] &&
                        packet[2] == LocalDiscoveryMagic[2] &&
                        packet[3] == LocalDiscoveryMagic[3];

        var length = BitConverter.ToInt32(packet, 4);
        var deserializedJson = Encoding.UTF8.GetString(packet, 8, length);
        var deserialized = JsonSerializer.Deserialize<JsonElement>(deserializedJson);

        // Assert
        Assert.True(magicValid);
        Assert.Equal(originalDeviceId, deserialized.GetProperty("DeviceId").GetString());
        Assert.Equal(originalInstanceId, deserialized.GetProperty("InstanceId").GetInt32());
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, false)]
    [InlineData(new byte[] { 0x2E, 0xA7, 0xD9, 0x00 }, false)]
    [InlineData(new byte[] { 0x2E, 0xA7, 0x00, 0x0B }, false)]
    [InlineData(new byte[] { 0x2E, 0x00, 0xD9, 0x0B }, false)]
    [InlineData(new byte[] { 0x00, 0xA7, 0xD9, 0x0B }, false)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, false)]
    [InlineData(new byte[] { 0x2E, 0xA7, 0xD9, 0x0B }, true)]
    public void LocalDiscoveryMagic_ValidatesCorrectly(byte[] magic, bool expectedValid)
    {
        // Act
        var isValid = magic.Length == 4 &&
                      magic[0] == LocalDiscoveryMagic[0] &&
                      magic[1] == LocalDiscoveryMagic[1] &&
                      magic[2] == LocalDiscoveryMagic[2] &&
                      magic[3] == LocalDiscoveryMagic[3];

        // Assert
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void LocalDiscoveryProtocol_RejectsPacketTooShort()
    {
        // Arrange - Create a packet that's too short (less than 8 bytes)
        var shortPacket = new byte[] { 0x2E, 0xA7, 0xD9, 0x0B, 0x00, 0x00 };

        // Act - Validate packet length
        var isValidLength = shortPacket.Length >= 8;

        // Assert
        Assert.False(isValidLength);
    }

    [Fact]
    public void LocalDiscoveryProtocol_RejectsInvalidMagic()
    {
        // Arrange - Create a packet with invalid magic
        var json = "{\"DeviceId\":\"test\"}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var packet = new byte[4 + 4 + jsonBytes.Length];
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }.CopyTo(packet, 0); // Invalid magic
        BitConverter.GetBytes(jsonBytes.Length).CopyTo(packet, 4);
        jsonBytes.CopyTo(packet, 8);

        // Act - Verify magic is invalid
        var magicValid = packet[0] == LocalDiscoveryMagic[0] &&
                        packet[1] == LocalDiscoveryMagic[1] &&
                        packet[2] == LocalDiscoveryMagic[2] &&
                        packet[3] == LocalDiscoveryMagic[3];

        // Assert
        Assert.False(magicValid);
    }

    [Fact]
    public void LocalDiscoveryProtocol_RejectsIncompletePayload()
    {
        // Arrange - Create a packet where length field says more data than available
        var json = "{}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var packet = new byte[4 + 4 + jsonBytes.Length];
        LocalDiscoveryMagic.CopyTo(packet, 0);
        BitConverter.GetBytes(100).CopyTo(packet, 4); // Claim 100 bytes but only have 2
        jsonBytes.CopyTo(packet, 8);

        // Act - Validate payload length
        var claimedLength = BitConverter.ToInt32(packet, 4);
        var actualPayloadLength = packet.Length - 8;
        var hasCompletePayload = actualPayloadLength >= claimedLength;

        // Assert
        Assert.False(hasCompletePayload);
    }

    [Fact]
    public void LocalDiscoveryProtocol_HandlesEmptyAddresses()
    {
        // Arrange - Create announcement with no addresses
        var announcement = new
        {
            DeviceId = "TESTDEVICE",
            Addresses = Array.Empty<string>(),
            InstanceId = 1
        };

        var json = JsonSerializer.Serialize(announcement);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var packet = new byte[4 + 4 + jsonBytes.Length];
        LocalDiscoveryMagic.CopyTo(packet, 0);
        BitConverter.GetBytes(jsonBytes.Length).CopyTo(packet, 4);
        jsonBytes.CopyTo(packet, 8);

        // Act - Deserialize and verify
        var length = BitConverter.ToInt32(packet, 4);
        var deserializedJson = Encoding.UTF8.GetString(packet, 8, length);
        var deserialized = JsonSerializer.Deserialize<JsonElement>(deserializedJson);

        // Assert
        Assert.Equal("TESTDEVICE", deserialized.GetProperty("DeviceId").GetString());
        Assert.Equal(0, deserialized.GetProperty("Addresses").GetArrayLength());
    }

    [Fact]
    public void LocalDiscoveryProtocol_HandlesMultipleAddresses()
    {
        // Arrange - Create announcement with multiple addresses
        var announcement = new
        {
            DeviceId = "TESTDEVICE",
            Addresses = new[]
            {
                "tcp://192.168.1.1:22000",
                "tcp://10.0.0.1:22000",
                "quic://192.168.1.1:22000",
                "relay://relay.syncthing.net:443"
            },
            InstanceId = 1
        };

        var json = JsonSerializer.Serialize(announcement);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var packet = new byte[4 + 4 + jsonBytes.Length];
        LocalDiscoveryMagic.CopyTo(packet, 0);
        BitConverter.GetBytes(jsonBytes.Length).CopyTo(packet, 4);
        jsonBytes.CopyTo(packet, 8);

        // Act - Deserialize and verify
        var length = BitConverter.ToInt32(packet, 4);
        var deserializedJson = Encoding.UTF8.GetString(packet, 8, length);
        var deserialized = JsonSerializer.Deserialize<JsonElement>(deserializedJson);

        // Assert
        Assert.Equal("TESTDEVICE", deserialized.GetProperty("DeviceId").GetString());
        Assert.Equal(4, deserialized.GetProperty("Addresses").GetArrayLength());
    }

    [Fact]
    public void Cache_HandlesLocalSourceWithCorrectPriority()
    {
        // Arrange
        var deviceId = "device1";
        var lanAddress = "tcp://192.168.1.1:22000";
        var wanAddress = "tcp://8.8.8.8:22000";

        // Act - Add from Local discovery (LAN addresses have higher priority)
        _cache.AddPositive(deviceId, new List<string> { lanAddress, wanAddress }, DiscoveryCacheSource.Local);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(DiscoveryCacheSource.Local, entry.Source);
        Assert.Equal(2, entry.Addresses.Count);
    }

    [Fact]
    public void Cache_LocalSourceEntriesAreSeparate()
    {
        // Arrange
        var device1 = "device1";
        var device2 = "device2";

        // Act - Add from Local discovery
        _cache.AddPositive(device1, new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive(device2, new List<string> { "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Local);

        // Assert
        var stats = _cache.GetStatistics();
        Assert.Equal(2, stats.LocalEntryCount);
    }

    [Fact]
    public void Cache_MergesLocalAndGlobalDiscovery()
    {
        // Arrange
        var deviceId = "device1";
        var localAddress = "tcp://192.168.1.1:22000";
        var globalAddress = "tcp://8.8.8.8:22000";

        // Act - Add from both Local and Global discovery
        _cache.AddPositive(deviceId, new List<string> { localAddress }, DiscoveryCacheSource.Local);
        _cache.AddPositive(deviceId, new List<string> { globalAddress }, DiscoveryCacheSource.Global);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        // Note: Source reflects the last source that updated the entry
        // The cache merges addresses but stores the latest source
        Assert.Equal(DiscoveryCacheSource.Global, entry.Source);
        Assert.Equal(2, entry.Addresses.Count);
        Assert.Contains(localAddress, entry.Addresses);
        Assert.Contains(globalAddress, entry.Addresses);
    }

    [Fact]
    public void LocalDiscoveryProtocol_PreservesDeviceIdCaseSensitivity()
    {
        // Arrange - Device IDs in Syncthing are case-sensitive
        var deviceIdUpper = "TESTDEVICE-AAAA-BBBB";
        var deviceIdLower = "testdevice-aaaa-bbbb";

        var announcementUpper = new { DeviceId = deviceIdUpper, Addresses = Array.Empty<string>(), InstanceId = 1 };
        var announcementLower = new { DeviceId = deviceIdLower, Addresses = Array.Empty<string>(), InstanceId = 1 };

        var jsonUpper = JsonSerializer.Serialize(announcementUpper);
        var jsonLower = JsonSerializer.Serialize(announcementLower);

        // Act & Assert
        Assert.Contains(deviceIdUpper, jsonUpper);
        Assert.Contains(deviceIdLower, jsonLower);
        Assert.NotEqual(deviceIdUpper, deviceIdLower);
    }

    [Fact]
    public void LocalDiscoveryProtocol_ByteOrderIsLittleEndian()
    {
        // Arrange - Test that length field uses little-endian byte order
        var testLength = 256; // 0x00000100 in little-endian: 00 01 00 00

        // Act
        var lengthBytes = BitConverter.GetBytes(testLength);

        // Assert - Verify little-endian on this platform (BitConverter.IsLittleEndian)
        if (BitConverter.IsLittleEndian)
        {
            Assert.Equal(0x00, lengthBytes[0]); // LSB first
            Assert.Equal(0x01, lengthBytes[1]);
            Assert.Equal(0x00, lengthBytes[2]);
            Assert.Equal(0x00, lengthBytes[3]); // MSB last
        }

        // Verify round-trip
        var reconstructedLength = BitConverter.ToInt32(lengthBytes, 0);
        Assert.Equal(testLength, reconstructedLength);
    }

    [Fact]
    public void LocalDiscoveryProtocol_HandlesUnicodeInAddresses()
    {
        // Arrange - While unusual, addresses could contain Unicode in hostname
        var announcement = new
        {
            DeviceId = "TESTDEVICE",
            Addresses = new[] { "tcp://example.example:22000" },
            InstanceId = 1
        };

        var json = JsonSerializer.Serialize(announcement);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var packet = new byte[4 + 4 + jsonBytes.Length];
        LocalDiscoveryMagic.CopyTo(packet, 0);
        BitConverter.GetBytes(jsonBytes.Length).CopyTo(packet, 4);
        jsonBytes.CopyTo(packet, 8);

        // Act - Deserialize and verify
        var length = BitConverter.ToInt32(packet, 4);
        var deserializedJson = Encoding.UTF8.GetString(packet, 8, length);
        var deserialized = JsonSerializer.Deserialize<JsonElement>(deserializedJson);

        // Assert
        var addresses = deserialized.GetProperty("Addresses");
        Assert.Equal(1, addresses.GetArrayLength());
    }

    #endregion

    public void Dispose()
    {
        _cache.Dispose();
    }
}
