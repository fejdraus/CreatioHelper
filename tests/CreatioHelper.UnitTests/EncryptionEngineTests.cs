using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Infrastructure.Services.Sync;

namespace CreatioHelper.Tests;

public class EncryptionEngineTests
{
    private readonly EncryptionEngine _encryptionEngine;
    private readonly Mock<ILogger<EncryptionEngine>> _mockLogger;

    public EncryptionEngineTests()
    {
        _mockLogger = new Mock<ILogger<EncryptionEngine>>();
        _encryptionEngine = new EncryptionEngine(_mockLogger.Object);
    }

    [Fact]
    public void EncryptBlock_EmptyData_ReturnsOriginalData()
    {
        // Arrange
        var emptyData = Array.Empty<byte>();
        var key = EncryptionEngine.GenerateKey();

        // Act
        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(emptyData, key);

        // Assert
        Assert.Equal(emptyData, encryptedData);
        Assert.False(isEncrypted);
    }

    [Fact]
    public void EncryptBlock_NullData_ReturnsEmptyArray()
    {
        // Arrange
        var key = EncryptionEngine.GenerateKey();

        // Act
        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(null!, key);

        // Assert
        Assert.Empty(encryptedData);
        Assert.False(isEncrypted);
    }

    [Fact]
    public void EncryptBlock_ValidData_EncryptsSuccessfully()
    {
        // Arrange
        var originalData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var key = EncryptionEngine.GenerateKey();

        // Act
        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(originalData, key);

        // Assert
        Assert.True(isEncrypted);
        Assert.NotEqual(originalData, encryptedData);
        Assert.True(encryptedData.Length > originalData.Length); // Should include nonce + tag
        
        // Should be: nonce (12 bytes) + encrypted_data (10 bytes) + tag (16 bytes) = 38 bytes
        Assert.Equal(originalData.Length + 12 + 16, encryptedData.Length);
    }

    [Fact]
    public void EncryptBlock_InvalidKey_ReturnsFalse()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var invalidKey = new byte[16]; // Wrong key size (should be 32)

        // Act
        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(data, invalidKey);

        // Assert
        Assert.False(isEncrypted);
        Assert.Equal(data, encryptedData);
    }

    [Fact]
    public void EncryptBlock_NullKey_ReturnsFalse()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(data, null!);

        // Assert
        Assert.False(isEncrypted);
        Assert.Equal(data, encryptedData);
    }

    [Fact]
    public void DecryptBlock_ValidEncryptedData_ReturnsOriginalData()
    {
        // Arrange
        var originalData = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var key = EncryptionEngine.GenerateKey();

        // First encrypt the data
        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(originalData, key);
        Assert.True(isEncrypted);

        // Act - decrypt the data
        var decryptedData = _encryptionEngine.DecryptBlock(encryptedData, key, originalData.Length);

        // Assert
        Assert.Equal(originalData, decryptedData);
    }

    [Fact]
    public void DecryptBlock_EmptyData_ReturnsEmptyArray()
    {
        // Arrange
        var key = EncryptionEngine.GenerateKey();

        // Act
        var decryptedData = _encryptionEngine.DecryptBlock(Array.Empty<byte>(), key);

        // Assert
        Assert.Empty(decryptedData);
    }

    [Fact]
    public void DecryptBlock_NullData_ReturnsEmptyArray()
    {
        // Arrange
        var key = EncryptionEngine.GenerateKey();

        // Act
        var decryptedData = _encryptionEngine.DecryptBlock(null!, key);

        // Assert
        Assert.Empty(decryptedData);
    }

    [Fact]
    public void DecryptBlock_InvalidKey_ThrowsException()
    {
        // Arrange
        var originalData = new byte[] { 1, 2, 3, 4, 5 };
        var correctKey = EncryptionEngine.GenerateKey();
        var wrongKey = EncryptionEngine.GenerateKey(); // Different key

        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(originalData, correctKey);
        Assert.True(isEncrypted);

        // Act & Assert
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            _encryptionEngine.DecryptBlock(encryptedData, wrongKey, originalData.Length));
    }

    [Fact]
    public void DecryptBlock_InvalidKeySize_ThrowsArgumentException()
    {
        // Arrange
        var encryptedData = new byte[50]; // Some dummy encrypted data
        var invalidKey = new byte[16]; // Wrong size

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            _encryptionEngine.DecryptBlock(encryptedData, invalidKey));
    }

    [Fact]
    public void DecryptBlock_TamperedData_ThrowsException()
    {
        // Arrange
        var originalData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var key = EncryptionEngine.GenerateKey();

        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(originalData, key);
        Assert.True(isEncrypted);

        // Tamper with the data
        encryptedData[15] ^= 0xFF; // Flip some bits

        // Act & Assert
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            _encryptionEngine.DecryptBlock(encryptedData, key, originalData.Length));
    }

    [Fact]
    public void EncryptDecryptRoundTrip_LargeData_MaintainsDataIntegrity()
    {
        // Arrange - large data
        var originalData = new byte[10000];
        for (int i = 0; i < originalData.Length; i++)
        {
            originalData[i] = (byte)(i % 256);
        }
        var key = EncryptionEngine.GenerateKey();

        // Act - encrypt and decrypt
        var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(originalData, key);
        Assert.True(isEncrypted);

        var decryptedData = _encryptionEngine.DecryptBlock(encryptedData, key, originalData.Length);

        // Assert
        Assert.Equal(originalData, decryptedData);
    }

    [Theory]
    [InlineData(1)]     // Very small
    [InlineData(10)]    // Small
    [InlineData(100)]   // Medium
    [InlineData(1000)]  // Large
    public void ShouldEncrypt_AnySize_ReturnsTrue(int size)
    {
        // Arrange
        var data = new byte[size];
        Random.Shared.NextBytes(data);

        // Act
        var shouldEncrypt = _encryptionEngine.ShouldEncrypt(data);

        // Assert
        Assert.True(shouldEncrypt); // Should encrypt ALL data regardless of size
    }

    [Fact]
    public void ShouldEncrypt_NullData_ReturnsFalse()
    {
        // Act
        var shouldEncrypt = _encryptionEngine.ShouldEncrypt(null!);

        // Assert
        Assert.False(shouldEncrypt);
    }

    [Fact]
    public void ShouldEncrypt_EmptyData_ReturnsFalse()
    {
        // Act
        var shouldEncrypt = _encryptionEngine.ShouldEncrypt(Array.Empty<byte>());

        // Assert
        Assert.False(shouldEncrypt);
    }

    [Fact]
    public void GenerateKey_ReturnsValidKey()
    {
        // Act
        var key = EncryptionEngine.GenerateKey();

        // Assert
        Assert.Equal(32, key.Length); // Should be 256 bits = 32 bytes
        Assert.NotEqual(new byte[32], key); // Should not be all zeros
    }

    [Fact]
    public void GenerateKey_MultipleKeys_AreUnique()
    {
        // Act
        var key1 = EncryptionEngine.GenerateKey();
        var key2 = EncryptionEngine.GenerateKey();

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveKeyFromPassword_ValidInput_ReturnsValidKey()
    {
        // Arrange
        var password = "test_password_123";
        var salt = EncryptionEngine.GenerateSalt();

        // Act
        var key = EncryptionEngine.DeriveKeyFromPassword(password, salt);

        // Assert
        Assert.Equal(32, key.Length);
        Assert.NotEqual(new byte[32], key); // Should not be all zeros
    }

    [Fact]
    public void DeriveKeyFromPassword_SameInputs_ReturnsSameKey()
    {
        // Arrange
        var password = "test_password";
        var salt = EncryptionEngine.GenerateSalt();

        // Act
        var key1 = EncryptionEngine.DeriveKeyFromPassword(password, salt);
        var key2 = EncryptionEngine.DeriveKeyFromPassword(password, salt);

        // Assert
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveKeyFromPassword_DifferentPasswords_ReturnsDifferentKeys()
    {
        // Arrange
        var salt = EncryptionEngine.GenerateSalt();

        // Act
        var key1 = EncryptionEngine.DeriveKeyFromPassword("password1", salt);
        var key2 = EncryptionEngine.DeriveKeyFromPassword("password2", salt);

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveKeyFromPassword_DifferentSalts_ReturnsDifferentKeys()
    {
        // Arrange
        var password = "same_password";

        // Act
        var key1 = EncryptionEngine.DeriveKeyFromPassword(password, EncryptionEngine.GenerateSalt());
        var key2 = EncryptionEngine.DeriveKeyFromPassword(password, EncryptionEngine.GenerateSalt());

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveKeyFromPassword_InvalidInputs_ThrowsArgumentException()
    {
        // Arrange
        var validSalt = EncryptionEngine.GenerateSalt();
        var invalidSalt = new byte[8]; // Too small

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            EncryptionEngine.DeriveKeyFromPassword("", validSalt)); // Empty password

        Assert.Throws<ArgumentException>(() =>
            EncryptionEngine.DeriveKeyFromPassword(null!, validSalt)); // Null password

        Assert.Throws<ArgumentException>(() =>
            EncryptionEngine.DeriveKeyFromPassword("password", invalidSalt)); // Invalid salt

        Assert.Throws<ArgumentException>(() =>
            EncryptionEngine.DeriveKeyFromPassword("password", null!)); // Null salt

        Assert.Throws<ArgumentException>(() =>
            EncryptionEngine.DeriveKeyFromPassword("password", validSalt, 5000)); // Too few iterations
    }

    [Fact]
    public void GenerateSalt_ReturnsValidSalt()
    {
        // Act
        var salt = EncryptionEngine.GenerateSalt();

        // Assert
        Assert.Equal(32, salt.Length); // Default size
        Assert.NotEqual(new byte[32], salt); // Should not be all zeros
    }

    [Fact]
    public void GenerateSalt_CustomSize_ReturnsCorrectSize()
    {
        // Act
        var salt = EncryptionEngine.GenerateSalt(16);

        // Assert
        Assert.Equal(16, salt.Length);
    }

    [Fact]
    public void GenerateSalt_MultipleSalts_AreUnique()
    {
        // Act
        var salt1 = EncryptionEngine.GenerateSalt();
        var salt2 = EncryptionEngine.GenerateSalt();

        // Assert
        Assert.NotEqual(salt1, salt2);
    }
}