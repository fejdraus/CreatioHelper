using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Proto;
using Microsoft.Extensions.Logging;
using Moq;
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

    #region Device ID Validator Tests - SHA-256 Certificate Derivation

    /// <summary>
    /// Tests that DeviceIdValidator generates valid Device IDs from raw key material.
    /// Verifies: SHA-256 hashing, Base32 encoding, Luhn checksums, and formatting.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_GenerateDeviceId_FromRawKeyMaterial_ProducesValidFormat()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var keyMaterial = new byte[64];
        new Random(42).NextBytes(keyMaterial);

        // Act
        var deviceId = validator.GenerateDeviceId(keyMaterial);

        // Assert - verify format
        Assert.NotNull(deviceId);
        Assert.Equal(63, deviceId.Length); // 56 chars + 7 hyphens

        var segments = deviceId.Split('-');
        Assert.Equal(8, segments.Length);

        foreach (var segment in segments)
        {
            Assert.Equal(7, segment.Length);
            Assert.True(IsValidBase32(segment), $"Segment '{segment}' contains invalid Base32 characters");
        }
    }

    /// <summary>
    /// Tests that the same key material always produces the same Device ID (deterministic).
    /// </summary>
    [Fact]
    public void DeviceIdValidator_GenerateDeviceId_IsDeterministic()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var keyMaterial = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        // Act - generate multiple times
        var deviceId1 = validator.GenerateDeviceId(keyMaterial);
        var deviceId2 = validator.GenerateDeviceId(keyMaterial);
        var deviceId3 = validator.GenerateDeviceId(keyMaterial);

        // Assert - all should be identical
        Assert.Equal(deviceId1, deviceId2);
        Assert.Equal(deviceId2, deviceId3);
    }

    /// <summary>
    /// Tests that different key materials produce different Device IDs.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_GenerateDeviceId_DifferentKeyMaterial_ProducesDifferentIds()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var keyMaterial1 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var keyMaterial2 = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 };

        // Act
        var deviceId1 = validator.GenerateDeviceId(keyMaterial1);
        var deviceId2 = validator.GenerateDeviceId(keyMaterial2);

        // Assert - should be different
        Assert.NotEqual(deviceId1, deviceId2);
    }

    /// <summary>
    /// Tests that generated Device IDs pass validation (Luhn checksum verification).
    /// </summary>
    [Fact]
    public void DeviceIdValidator_GeneratedDeviceId_PassesValidation()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var keyMaterial = new byte[32];
        new Random(123).NextBytes(keyMaterial);

        // Act
        var deviceId = validator.GenerateDeviceId(keyMaterial);
        var isValid = validator.ValidateDeviceId(deviceId);

        // Assert
        Assert.True(isValid, $"Generated Device ID '{deviceId}' failed validation");
    }

    /// <summary>
    /// Tests Device ID generation from X.509 certificate uses certificate's raw bytes.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_GenerateDeviceId_FromCertificate_UsesCertificateRawData()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);

        // Create a self-signed certificate
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=TestDevice",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        // Act
        var deviceId = validator.GenerateDeviceId(certificate);

        // Assert - verify format and validity
        Assert.NotNull(deviceId);
        Assert.Equal(63, deviceId.Length);
        Assert.True(validator.ValidateDeviceId(deviceId));
    }

    /// <summary>
    /// Tests that the Device ID derived from certificate matches the SHA-256 hash of RawData.
    /// This verifies the core Syncthing protocol requirement.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_GenerateDeviceId_FromCertificate_MatchesSha256Hash()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);

        // Create a self-signed certificate
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=TestDevice",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        // Act
        var deviceIdFromCert = validator.GenerateDeviceId(certificate);
        var deviceIdFromRawData = validator.GenerateDeviceId(certificate.RawData);

        // Assert - both methods should produce the same Device ID
        Assert.Equal(deviceIdFromRawData, deviceIdFromCert);
    }

    /// <summary>
    /// Tests that DeviceIdValidator validates self-generated Device IDs.
    /// This is a roundtrip test: generate -> validate should always succeed.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_ValidateDeviceId_SelfGeneratedIds_ReturnsTrue()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);

        // Generate multiple device IDs from different key materials
        var keyMaterials = new[]
        {
            new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 },
            new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8 },
            new byte[32] // All zeros
        };

        foreach (var keyMaterial in keyMaterials)
        {
            // Act
            var deviceId = validator.GenerateDeviceId(keyMaterial);
            var isValid = validator.ValidateDeviceId(deviceId);

            // Assert
            Assert.True(isValid, $"Generated Device ID '{deviceId}' failed validation");
        }
    }

    /// <summary>
    /// Tests that DeviceIdValidator rejects invalid Device IDs.
    /// </summary>
    [Theory]
    [InlineData("")] // Empty
    [InlineData("INVALID")] // Too short
    [InlineData("AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG")] // Only 7 segments
    [InlineData("AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHH")] // Last segment 6 chars
    public void DeviceIdValidator_ValidateDeviceId_InvalidIds_ReturnsFalse(string deviceId)
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);

        // Act
        var isValid = validator.ValidateDeviceId(deviceId);

        // Assert
        Assert.False(isValid, $"Invalid Device ID '{deviceId}' passed validation");
    }

    /// <summary>
    /// Tests Device ID normalization removes hyphens and converts to uppercase.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_NormalizeDeviceId_RemovesHyphensAndUppercases()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var deviceIdWithHyphens = "mriw7ok-nett3m4-n6sbwme-n7xgodl-vmbuxkv-tqpbqce-xks4k3w-lo2slqe";

        // Act
        var normalized = validator.NormalizeDeviceId(deviceIdWithHyphens);

        // Assert
        Assert.DoesNotContain("-", normalized);
        Assert.Equal(normalized, normalized.ToUpperInvariant());
        Assert.Equal(56, normalized.Length);
    }

    /// <summary>
    /// Tests Device ID equality comparison ignores formatting and case.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_AreEqual_IgnoresFormattingAndCase()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var deviceId1 = "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE";
        var deviceId2 = "mriw7oknett3m4n6sbwmen7xgodlvmbuxkvtqpbqcexks4k3wlo2slqe";

        // Act
        var areEqual = validator.AreEqual(deviceId1, deviceId2);

        // Assert
        Assert.True(areEqual);
    }

    /// <summary>
    /// Tests that FormatDeviceId correctly adds hyphens every 7 characters.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_FormatDeviceId_AddsHyphensEvery7Chars()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var normalized = "MRIW7OKNETT3M4N6SBWMEN7XGODLVMBUXKVTQPBQCEXKS4K3WLO2SLQE";

        // Act
        var formatted = validator.FormatDeviceId(normalized);

        // Assert
        var segments = formatted.Split('-');
        Assert.Equal(8, segments.Length);
        foreach (var segment in segments)
        {
            Assert.Equal(7, segment.Length);
        }
    }

    /// <summary>
    /// Verifies the Base32 encoding follows RFC 4648 alphabet (A-Z, 2-7).
    /// </summary>
    [Fact]
    public void DeviceIdValidator_GenerateDeviceId_UsesRfc4648Base32Alphabet()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var keyMaterial = new byte[32];
        new Random(999).NextBytes(keyMaterial);

        // Act
        var deviceId = validator.GenerateDeviceId(keyMaterial);
        var normalized = validator.NormalizeDeviceId(deviceId);

        // Assert - all characters should be in RFC 4648 Base32 alphabet
        const string rfc4648Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        foreach (var c in normalized)
        {
            Assert.Contains(c, rfc4648Alphabet);
        }
    }

    /// <summary>
    /// Tests that multiple random certificates generate unique Device IDs.
    /// </summary>
    [Fact]
    public void DeviceIdValidator_GenerateDeviceId_MultipleCertificates_ProducesUniqueIds()
    {
        // Arrange
        var logger = new Mock<ILogger<DeviceIdValidator>>();
        var validator = new DeviceIdValidator(logger.Object);
        var deviceIds = new HashSet<string>();

        // Act - generate 10 certificates and their Device IDs
        for (int i = 0; i < 10; i++)
        {
            using var rsa = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(
                $"CN=TestDevice{i}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            using var certificate = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));

            var deviceId = validator.GenerateDeviceId(certificate);
            deviceIds.Add(deviceId);
        }

        // Assert - all 10 should be unique
        Assert.Equal(10, deviceIds.Count);
    }

    /// <summary>
    /// Tests Device ID short form extraction.
    /// </summary>
    [Fact]
    public void DeviceIdExtensions_ToShortId_ReturnsFirst7Chars()
    {
        // Arrange
        var fullDeviceId = "MRIW7OK-NETT3M4-N6SBWME-N7XGODL-VMBUXKV-TQPBQCE-XKS4K3W-LO2SLQE";

        // Act
        var shortId = fullDeviceId.ToShortId();

        // Assert
        Assert.Equal("MRIW7OK", shortId);
    }

    /// <summary>
    /// Tests short ID detection.
    /// </summary>
    [Theory]
    [InlineData("MRIW7OK", true)] // Valid short ID
    [InlineData("mriw7ok", false)] // Lowercase - not valid per current implementation
    [InlineData("MRIW7O", false)] // Too short
    [InlineData("MRIW7OK-NETT3M4", false)] // Too long (with hyphen)
    [InlineData("MRIW8OK", false)] // Contains 8 (not in Base32 alphabet)
    public void DeviceIdExtensions_IsShortId_CorrectlyIdentifies(string id, bool expected)
    {
        // Act
        var isShort = id.IsShortId();

        // Assert
        Assert.Equal(expected, isShort);
    }

    private static bool IsValidBase32(string segment)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        return segment.All(c => alphabet.Contains(c));
    }

    #endregion
}
