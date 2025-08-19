using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Infrastructure.Services.Sync;
using System.IO;

namespace CreatioHelper.Tests;

public class KeyManagerTests : IDisposable
{
    private readonly KeyManager _keyManager;
    private readonly Mock<ILogger<KeyManager>> _mockLogger;
    private readonly string _testKeyStorePath;

    public KeyManagerTests()
    {
        _mockLogger = new Mock<ILogger<KeyManager>>();
        _testKeyStorePath = Path.Combine(Path.GetTempPath(), "CreatioHelper_Tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(_testKeyStorePath)!);
        
        _keyManager = new KeyManager(_mockLogger.Object, "TEST-CURRENT-DEVICE", _testKeyStorePath);
    }

    public void Dispose()
    {
        try
        {
            var testDir = Path.GetDirectoryName(_testKeyStorePath);
            if (testDir != null && Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public async Task GetOrCreateDeviceKeyAsync_NewDevice_GeneratesKey()
    {
        // Arrange
        var deviceId = "test-device-123";

        // Act
        var key = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId);

        // Assert
        Assert.Equal(32, key.Length); // 256-bit key
        Assert.NotEqual(new byte[32], key); // Should not be all zeros
    }

    [Fact]
    public async Task GetOrCreateDeviceKeyAsync_SameDevice_ReturnsSameKey()
    {
        // Arrange
        var deviceId = "test-device-456";

        // Act
        var key1 = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId);
        var key2 = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId);

        // Assert
        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task GetOrCreateDeviceKeyAsync_DifferentDevices_ReturnsDifferentKeys()
    {
        // Arrange
        var deviceId1 = "device-1";
        var deviceId2 = "device-2";

        // Act
        var key1 = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId1);
        var key2 = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId2);

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task GetOrCreateDeviceKeyAsync_WithPassword_GeneratesPasswordDerivedKey()
    {
        // Arrange
        var deviceId = "password-device";
        var password = "test-password-123";

        // Act
        var key = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId, password);

        // Assert
        Assert.Equal(32, key.Length);
        Assert.NotEqual(new byte[32], key);

        // Should return same key for same password
        var key2 = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId, password);
        Assert.Equal(key, key2);
    }

    [Fact]
    public async Task SetDeviceKeyAsync_ValidKey_SetsKey()
    {
        // Arrange
        var deviceId = "manual-device";
        var manualKey = EncryptionEngine.GenerateKey();

        // Act
        await _keyManager.SetDeviceKeyAsync(deviceId, manualKey);
        var retrievedKey = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId);

        // Assert
        Assert.Equal(manualKey, retrievedKey);
    }

    [Fact]
    public async Task SetDeviceKeyAsync_InvalidKeySize_ThrowsArgumentException()
    {
        // Arrange
        var deviceId = "invalid-key-device";
        var invalidKey = new byte[16]; // Wrong size

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _keyManager.SetDeviceKeyAsync(deviceId, invalidKey));
    }

    [Fact]
    public async Task SetDeviceKeyAsync_NullKey_ThrowsArgumentException()
    {
        // Arrange
        var deviceId = "null-key-device";

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _keyManager.SetDeviceKeyAsync(deviceId, null!));
    }

    [Fact]
    public async Task RemoveDeviceKeyAsync_ExistingDevice_RemovesKey()
    {
        // Arrange
        var deviceId = "removable-device";
        await _keyManager.GetOrCreateDeviceKeyAsync(deviceId); // Create key first
        
        Assert.True(_keyManager.HasDeviceKey(deviceId));

        // Act
        await _keyManager.RemoveDeviceKeyAsync(deviceId);

        // Assert
        Assert.False(_keyManager.HasDeviceKey(deviceId));
    }

    [Fact]
    public async Task RemoveDeviceKeyAsync_NonExistentDevice_DoesNotThrow()
    {
        // Arrange
        var deviceId = "non-existent-device";

        // Act & Assert - should not throw
        await _keyManager.RemoveDeviceKeyAsync(deviceId);
    }

    [Fact]
    public async Task HasDeviceKey_ExistingDevice_ReturnsTrue()
    {
        // Arrange
        var deviceId = "existing-device";
        await _keyManager.GetOrCreateDeviceKeyAsync(deviceId);

        // Act & Assert
        Assert.True(_keyManager.HasDeviceKey(deviceId));
    }

    [Fact]
    public void HasDeviceKey_NonExistentDevice_ReturnsFalse()
    {
        // Arrange
        var deviceId = "non-existent-device";

        // Act & Assert
        Assert.False(_keyManager.HasDeviceKey(deviceId));
    }

    [Fact]
    public void HasDeviceKey_NullOrEmptyDeviceId_ReturnsFalse()
    {
        // Act & Assert
        Assert.False(_keyManager.HasDeviceKey(null!));
        Assert.False(_keyManager.HasDeviceKey(string.Empty));
    }

    [Fact]
    public async Task GetDeviceKeyMetadata_MultipleDevices_ReturnsCorrectMetadata()
    {
        // Arrange
        var deviceId1 = "device-meta-1";
        var deviceId2 = "device-meta-2";
        var passwordDevice = "password-meta-device";

        await _keyManager.GetOrCreateDeviceKeyAsync(deviceId1);
        await _keyManager.GetOrCreateDeviceKeyAsync(deviceId2);
        await _keyManager.GetOrCreateDeviceKeyAsync(passwordDevice, "test-password");

        // Act
        var metadata = _keyManager.GetDeviceKeyMetadata();

        // Assert
        Assert.Equal(3, metadata.Count);
        
        Assert.Contains(deviceId1, metadata.Keys);
        Assert.Contains(deviceId2, metadata.Keys);
        Assert.Contains(passwordDevice, metadata.Keys);

        Assert.False(metadata[deviceId1].IsPasswordDerived);
        Assert.False(metadata[deviceId2].IsPasswordDerived);
        Assert.True(metadata[passwordDevice].IsPasswordDerived);

        // Check that timestamps are reasonable (within last minute)
        var now = DateTime.UtcNow;
        Assert.True(metadata[deviceId1].CreatedAt > now.AddMinutes(-1));
        Assert.True(metadata[deviceId1].CreatedAt <= now);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task GetOrCreateDeviceKeyAsync_InvalidDeviceId_ThrowsArgumentException(string? deviceId)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _keyManager.GetOrCreateDeviceKeyAsync(deviceId!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task SetDeviceKeyAsync_InvalidDeviceId_ThrowsArgumentException(string? deviceId)
    {
        // Arrange
        var validKey = EncryptionEngine.GenerateKey();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _keyManager.SetDeviceKeyAsync(deviceId!, validKey));
    }

    [Fact]
    public async Task KeyPersistence_RestartKeyManager_RetainsKeys()
    {
        // Arrange
        var deviceId = "persistent-device";
        var originalKey = await _keyManager.GetOrCreateDeviceKeyAsync(deviceId);

        // Create a new KeyManager instance with same storage path
        var newKeyManager = new KeyManager(_mockLogger.Object, _testKeyStorePath);

        // Act
        var retrievedKey = await newKeyManager.GetOrCreateDeviceKeyAsync(deviceId);

        // Assert
        Assert.Equal(originalKey, retrievedKey);
    }

    [Fact]
    public async Task SetDeviceKeyAsync_WithoutPersistence_DoesNotPersistToStorage()
    {
        // Arrange
        var deviceId = "temp-device";
        var tempKey = EncryptionEngine.GenerateKey();

        // Act
        await _keyManager.SetDeviceKeyAsync(deviceId, tempKey, persist: false);

        // Create new KeyManager to test persistence
        var newKeyManager = new KeyManager(_mockLogger.Object, _testKeyStorePath);

        // Assert
        Assert.False(newKeyManager.HasDeviceKey(deviceId));
    }

    [Fact]
    public async Task ConcurrentAccess_SameDevice_ReturnsSameKey()
    {
        // Arrange
        var deviceId = "concurrent-device";
        var tasks = new List<Task<byte[]>>();

        // Act - start multiple concurrent key retrievals
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(_keyManager.GetOrCreateDeviceKeyAsync(deviceId));
        }

        var keys = await Task.WhenAll(tasks);

        // Assert - all keys should be identical
        var firstKey = keys[0];
        foreach (var key in keys)
        {
            Assert.Equal(firstKey, key);
        }
    }
}