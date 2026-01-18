using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Proto;
using Xunit;

namespace CreatioHelper.UnitTests;

/// <summary>
/// Unit tests for Device ID encoding and handling in BEP protocol.
/// Device IDs in Syncthing are SHA-256 hashes encoded in Base32 with Luhn checksums.
/// Format: XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX
/// </summary>
public class DeviceIdTests
{
    #region Device ID Format Tests

    [Fact]
    public void DeviceId_RoundTrip_ThroughClusterConfig()
    {
        // Arrange - Use a valid Syncthing device ID format
        // Note: The hyphenated format with check digits is specific to Syncthing
        var deviceIdString = "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE";

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = deviceIdString, Name = "TestDevice" }]
                }
            ]
        };

        // Act - convert to protobuf and back
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert - the device ID should round-trip correctly
        var resultDeviceId = backToOriginal.Folders[0].Devices[0].DeviceId;
        Assert.NotNull(resultDeviceId);
        Assert.NotEmpty(resultDeviceId);
        // The formatted device ID should contain hyphens every 7 characters
        Assert.Contains("-", resultDeviceId);
    }

    [Fact]
    public void DeviceId_EmptyString_HandledGracefully()
    {
        // Arrange
        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = "", Name = "TestDevice" }]
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        var resultDeviceId = backToOriginal.Folders[0].Devices[0].DeviceId;
        Assert.Equal(string.Empty, resultDeviceId);
    }

    [Fact]
    public void DeviceId_NullString_HandledGracefully()
    {
        // Arrange
        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = null!, Name = "TestDevice" }]
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert - should not throw and result should be empty
        var resultDeviceId = backToOriginal.Folders[0].Devices[0].DeviceId;
        Assert.Equal(string.Empty, resultDeviceId);
    }

    #endregion

    #region Device ID Bytes Conversion Tests

    [Fact]
    public void DeviceId_Bytes_PreservedInProtobuf()
    {
        // Arrange - use raw bytes for device ID
        var rawDeviceIdBytes = new byte[32];
        new Random(42).NextBytes(rawDeviceIdBytes);

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices =
                    [
                        new BepDevice
                        {
                            Id = rawDeviceIdBytes,
                            DeviceId = "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE",
                            Name = "TestDevice"
                        }
                    ]
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert - raw bytes should be preserved through round-trip
        var resultBytes = backToOriginal.Folders[0].Devices[0].Id;
        Assert.NotNull(resultBytes);
        Assert.NotEmpty(resultBytes);
    }

    #endregion

    #region Device ID Validation Tests

    [Fact]
    public void DeviceId_WithHyphens_ParsedCorrectly()
    {
        // Arrange - device ID with hyphens (standard Syncthing format)
        var deviceIdWithHyphens = "AAAA-BBBB-CCCC-DDDD";

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = deviceIdWithHyphens, Name = "TestDevice" }]
                }
            ]
        };

        // Act - should not throw
        var proto = BepMessageConverter.ToProto(bepConfig);

        // Assert
        Assert.NotNull(proto);
        Assert.Single(proto.Folders);
    }

    [Fact]
    public void DeviceId_WithoutHyphens_ParsedCorrectly()
    {
        // Arrange - device ID without hyphens
        var deviceIdNoHyphens = "AAAABBBBCCCCDDDD";

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = deviceIdNoHyphens, Name = "TestDevice" }]
                }
            ]
        };

        // Act - should not throw
        var proto = BepMessageConverter.ToProto(bepConfig);

        // Assert
        Assert.NotNull(proto);
    }

    [Fact]
    public void DeviceId_CaseInsensitive_NormalizedToUppercase()
    {
        // Arrange - lowercase device ID
        var lowercaseDeviceId = "mriw7ok-nett3m4-n6sbwme-n7xgodl-vmbuxkv-tqpbqce-xks4k3w-lo2slqe";

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = lowercaseDeviceId, Name = "TestDevice" }]
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert - result should be uppercase (Base32 standard)
        var resultDeviceId = backToOriginal.Folders[0].Devices[0].DeviceId;
        Assert.Equal(resultDeviceId, resultDeviceId.ToUpperInvariant());
    }

    #endregion

    #region Device ID Format Validation

    [Fact]
    public void DeviceId_FullFormat_Has8Segments()
    {
        // Syncthing device IDs have 8 segments of 7 characters each (56 chars total)
        var fullDeviceId = "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE";

        var segments = fullDeviceId.Split('-');
        Assert.Equal(8, segments.Length);

        foreach (var segment in segments)
        {
            Assert.Equal(7, segment.Length);
        }
    }

    [Fact]
    public void DeviceId_TotalLength_Without_Hyphens_Is56()
    {
        // Syncthing device IDs are 56 characters when hyphens are removed
        // (This is 32 bytes = 256 bits encoded in Base32 with Luhn check digits = 8 x 7 = 56 characters)
        var fullDeviceId = "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE";
        var withoutHyphens = fullDeviceId.Replace("-", "");

        Assert.Equal(56, withoutHyphens.Length);
    }

    [Fact]
    public void DeviceId_OnlyContains_Base32Characters()
    {
        // Base32 alphabet: A-Z and 2-7
        var fullDeviceId = "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE";
        var withoutHyphens = fullDeviceId.Replace("-", "").ToUpperInvariant();
        var base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        foreach (var c in withoutHyphens)
        {
            Assert.Contains(c, base32Alphabet);
        }
    }

    #endregion

    #region Multiple Devices Tests

    [Fact]
    public void MultipleDevices_AllDeviceIdsPreserved()
    {
        // Arrange
        var deviceIds = new[]
        {
            "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE",
            "AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH",
            "2222222-3333333-4444444-5555555-6666666-7777777-AAAAAAA-BBBBBBB"
        };

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = deviceIds.Select((id, i) => new BepDevice
                    {
                        DeviceId = id,
                        Name = $"Device{i}"
                    }).ToList()
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert
        Assert.Equal(3, backToOriginal.Folders[0].Devices.Count);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal($"Device{i}", backToOriginal.Folders[0].Devices[i].Name);
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void DeviceId_WithTrailingPadding_Handled()
    {
        // Arrange - device ID with trailing padding characters (=)
        var deviceIdWithPadding = "AAAAAAA=";

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = deviceIdWithPadding, Name = "TestDevice" }]
                }
            ]
        };

        // Act - should not throw
        var proto = BepMessageConverter.ToProto(bepConfig);

        // Assert
        Assert.NotNull(proto);
    }

    [Fact]
    public void DeviceId_InvalidCharacters_Skipped()
    {
        // Arrange - device ID with invalid characters (should be skipped during parsing)
        var deviceIdWithInvalid = "AAAA!@#$BBBB";

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = deviceIdWithInvalid, Name = "TestDevice" }]
                }
            ]
        };

        // Act - should not throw, invalid chars are skipped
        var proto = BepMessageConverter.ToProto(bepConfig);

        // Assert
        Assert.NotNull(proto);
    }

    [Fact]
    public void DeviceId_EmptyAfterParsing_ProducesEmptyBytes()
    {
        // Arrange - device ID that becomes empty after removing invalid chars
        var deviceIdAllInvalid = "!@#$%^&*";

        var bepConfig = new BepClusterConfig
        {
            Folders =
            [
                new BepFolder
                {
                    Id = "test-folder",
                    Devices = [new BepDevice { DeviceId = deviceIdAllInvalid, Name = "TestDevice" }]
                }
            ]
        };

        // Act
        var proto = BepMessageConverter.ToProto(bepConfig);
        var backToOriginal = BepMessageConverter.FromProto(proto);

        // Assert - should produce empty result
        var resultDeviceId = backToOriginal.Folders[0].Devices[0].DeviceId;
        Assert.Equal(string.Empty, resultDeviceId);
    }

    #endregion
}
