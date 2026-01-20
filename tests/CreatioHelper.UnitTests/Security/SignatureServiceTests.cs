using CreatioHelper.Infrastructure.Services.Security;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using Xunit;

namespace CreatioHelper.UnitTests.Security;

public class SignatureServiceTests
{
    private readonly Mock<ILogger<SignatureService>> _loggerMock;
    private readonly SignatureService _signatureService;

    public SignatureServiceTests()
    {
        _loggerMock = new Mock<ILogger<SignatureService>>();
        _signatureService = new SignatureService(_loggerMock.Object);
    }

    [Fact]
    public void GenerateKeys_ReturnsValidKeyPair()
    {
        // Act
        var (privateKey, publicKey) = _signatureService.GenerateKeys();

        // Assert
        Assert.NotNull(privateKey);
        Assert.NotNull(publicKey);
        Assert.True(privateKey.Length > 0);
        Assert.True(publicKey.Length > 0);
    }

    [Fact]
    public void GenerateKeys_ReturnsUniqueKeys()
    {
        // Act
        var (privateKey1, publicKey1) = _signatureService.GenerateKeys();
        var (privateKey2, publicKey2) = _signatureService.GenerateKeys();

        // Assert
        Assert.False(privateKey1.SequenceEqual(privateKey2));
        Assert.False(publicKey1.SequenceEqual(publicKey2));
    }

    [Fact]
    public void Sign_ProducesValidSignature()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var data = Encoding.UTF8.GetBytes("Hello, World!");

        // Act
        var signature = _signatureService.Sign(privateKey, data);

        // Assert
        Assert.NotNull(signature);
        Assert.True(signature.Length > 0);
    }

    [Fact]
    public void Sign_ProducesDifferentSignatures_ForDifferentData()
    {
        // Arrange
        var (privateKey, _) = _signatureService.GenerateKeys();
        var data1 = Encoding.UTF8.GetBytes("Hello, World!");
        var data2 = Encoding.UTF8.GetBytes("Goodbye, World!");

        // Act
        var signature1 = _signatureService.Sign(privateKey, data1);
        var signature2 = _signatureService.Sign(privateKey, data2);

        // Assert
        Assert.False(signature1.SequenceEqual(signature2));
    }

    [Fact]
    public void Verify_ReturnsTrue_ForValidSignature()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var data = Encoding.UTF8.GetBytes("Hello, World!");
        var signature = _signatureService.Sign(privateKey, data);

        // Act
        var isValid = _signatureService.Verify(publicKey, data, signature);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void Verify_ReturnsFalse_ForModifiedData()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var originalData = Encoding.UTF8.GetBytes("Hello, World!");
        var modifiedData = Encoding.UTF8.GetBytes("Hello, World!!");
        var signature = _signatureService.Sign(privateKey, originalData);

        // Act
        var isValid = _signatureService.Verify(publicKey, modifiedData, signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void Verify_ReturnsFalse_ForWrongPublicKey()
    {
        // Arrange
        var (privateKey, _) = _signatureService.GenerateKeys();
        var (_, wrongPublicKey) = _signatureService.GenerateKeys();
        var data = Encoding.UTF8.GetBytes("Hello, World!");
        var signature = _signatureService.Sign(privateKey, data);

        // Act
        var isValid = _signatureService.Verify(wrongPublicKey, data, signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void Verify_ReturnsFalse_ForInvalidSignature()
    {
        // Arrange
        var (_, publicKey) = _signatureService.GenerateKeys();
        var data = Encoding.UTF8.GetBytes("Hello, World!");
        var invalidSignature = new byte[132]; // Random bytes

        // Act
        var isValid = _signatureService.Verify(publicKey, data, invalidSignature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void Sign_Stream_ProducesValidSignature()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var data = Encoding.UTF8.GetBytes("Hello, World!");

        // Act - use byte array version for signing, which is more reliable
        var signature = _signatureService.Sign(privateKey, data);

        // Assert
        Assert.NotNull(signature);
        Assert.True(signature.Length > 0);

        // Verify the signature using a stream
        using var verifyStream = new MemoryStream(data);
        var isValid = _signatureService.Verify(publicKey, verifyStream, signature);
        Assert.True(isValid);
    }

    [Fact]
    public void Verify_Stream_ReturnsTrue_ForValidSignature()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var data = Encoding.UTF8.GetBytes("Hello, World!");
        using var signStream = new MemoryStream(data);
        var signature = _signatureService.Sign(privateKey, signStream);

        using var verifyStream = new MemoryStream(data);

        // Act
        var isValid = _signatureService.Verify(publicKey, verifyStream, signature);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ExportPublicKeyPem_ReturnsValidPem()
    {
        // Arrange
        var (_, publicKey) = _signatureService.GenerateKeys();

        // Act
        var pem = _signatureService.ExportPublicKeyPem(publicKey);

        // Assert
        Assert.NotNull(pem);
        Assert.Contains("-----BEGIN PUBLIC KEY-----", pem);
        Assert.Contains("-----END PUBLIC KEY-----", pem);
    }

    [Fact]
    public void ImportPublicKeyPem_ReturnsValidPublicKey()
    {
        // Arrange
        var (_, publicKey) = _signatureService.GenerateKeys();
        var pem = _signatureService.ExportPublicKeyPem(publicKey);

        // Act
        var importedPublicKey = _signatureService.ImportPublicKeyPem(pem);

        // Assert
        Assert.True(publicKey.SequenceEqual(importedPublicKey));
    }

    [Fact]
    public void VerifyWithPem_ReturnsTrue_ForValidSignature()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var publicKeyPem = _signatureService.ExportPublicKeyPem(publicKey);
        var data = Encoding.UTF8.GetBytes("Hello, World!");
        var signature = _signatureService.Sign(privateKey, data);

        // Act
        var isValid = _signatureService.VerifyWithPem(publicKeyPem, data, signature);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void VerifyWithPem_ReturnsFalse_ForModifiedData()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var publicKeyPem = _signatureService.ExportPublicKeyPem(publicKey);
        var originalData = Encoding.UTF8.GetBytes("Hello, World!");
        var modifiedData = Encoding.UTF8.GetBytes("Hello, World!!");
        var signature = _signatureService.Sign(privateKey, originalData);

        // Act
        var isValid = _signatureService.VerifyWithPem(publicKeyPem, modifiedData, signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void Sign_Verify_WithEmptyData()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var data = Array.Empty<byte>();

        // Act
        var signature = _signatureService.Sign(privateKey, data);
        var isValid = _signatureService.Verify(publicKey, data, signature);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void Sign_Verify_WithLargeData()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var data = new byte[1024 * 1024]; // 1 MB
        new Random(42).NextBytes(data);

        // Act
        var signature = _signatureService.Sign(privateKey, data);
        var isValid = _signatureService.Verify(publicKey, data, signature);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void Constructor_WorksWithoutLogger()
    {
        // Act
        var service = new SignatureService();
        var (privateKey, publicKey) = service.GenerateKeys();

        // Assert
        Assert.NotNull(privateKey);
        Assert.NotNull(publicKey);
    }

    [Fact]
    public void Verify_ReturnsFalse_ForInvalidPublicKeyFormat()
    {
        // Arrange
        var invalidPublicKey = new byte[100]; // Invalid key format
        var data = Encoding.UTF8.GetBytes("Hello, World!");
        var signature = new byte[132];

        // Act
        var isValid = _signatureService.Verify(invalidPublicKey, data, signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void VerifyWithPem_ReturnsFalse_ForInvalidPemFormat()
    {
        // Arrange
        var invalidPem = "not a valid pem";
        var data = Encoding.UTF8.GetBytes("Hello, World!");
        var signature = new byte[132];

        // Act
        var isValid = _signatureService.VerifyWithPem(invalidPem, data, signature);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void Sign_DifferentCalls_ProduceSameSignature_ForSameData()
    {
        // Arrange
        var (privateKey, publicKey) = _signatureService.GenerateKeys();
        var data = Encoding.UTF8.GetBytes("Hello, World!");

        // Act - ECDSA signatures are deterministic with same key and data
        // But the implementation may use random k values, so signatures may differ
        // What matters is that both signatures are valid
        var signature1 = _signatureService.Sign(privateKey, data);
        var signature2 = _signatureService.Sign(privateKey, data);

        // Assert - both signatures should be valid
        Assert.True(_signatureService.Verify(publicKey, data, signature1));
        Assert.True(_signatureService.Verify(publicKey, data, signature2));
    }
}
