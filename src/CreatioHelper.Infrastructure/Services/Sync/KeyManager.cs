using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Key management for encryption (Syncthing-style without TLS)
/// Manages device-specific encryption keys for secure communication
/// </summary>
public class KeyManager
{
    private readonly ILogger<KeyManager> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _deviceKeys = new();
    private readonly ConcurrentDictionary<string, DeviceKeyInfo> _keyMetadata = new();
    private readonly string _keyStorePath;
    private readonly string _currentDeviceId;
    
    // Key derivation constants
    private const int SaltSize = 32;
    private const int KeyIterations = 100000;

    public KeyManager(ILogger<KeyManager> logger, string currentDeviceId, string? keyStorePath = null)
    {
        _logger = logger;
        _currentDeviceId = currentDeviceId ?? throw new ArgumentNullException(nameof(currentDeviceId));
        _keyStorePath = keyStorePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "CreatioHelper", "keys");
        
        Directory.CreateDirectory(Path.GetDirectoryName(_keyStorePath)!);
    }

    /// <summary>
    /// Gets or generates an encryption key for a folder using Syncthing-style derivation
    /// </summary>
    /// <param name="folderId">Folder ID</param>
    /// <param name="password">Folder password for key derivation</param>
    /// <returns>32-byte encryption key</returns>
    public async Task<byte[]> GetOrCreateFolderKeyAsync(string folderId, string password)
    {
        if (string.IsNullOrEmpty(folderId))
            throw new ArgumentException("Folder ID cannot be null or empty", nameof(folderId));
        
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be null or empty", nameof(password));

        _logger.LogInformation("🔑 KeyManager: GetOrCreateFolderKeyAsync called for folder {FolderId}", folderId);

        // Use folder ID as cache key
        var cacheKey = $"folder:{folderId}";
        
        // Check if we already have a key in memory
        if (_deviceKeys.TryGetValue(cacheKey, out var existingKey))
        {
            _logger.LogDebug("Using cached encryption key for folder {FolderId}", folderId);
            return existingKey;
        }

        _logger.LogInformation("🔄 KeyManager: Generating Syncthing-style folder key for folder {FolderId}", folderId);

        // Generate new key using Syncthing algorithm: scrypt(password, folderId as salt)
        var key = await GenerateFolderKeyAsync(folderId, password);
        _deviceKeys[cacheKey] = key;
        
        _logger.LogInformation("Generated new encryption key for folder {FolderId}", folderId);
        return key;
    }

    /// <summary>
    /// Explicitly sets an encryption key for a device (for testing or manual key exchange)
    /// </summary>
    /// <param name="deviceId">Target device ID</param>
    /// <param name="key">32-byte encryption key</param>
    /// <param name="persist">Whether to save the key to persistent storage</param>
    public async Task SetDeviceKeyAsync(string deviceId, byte[] key, bool persist = true)
    {
        if (string.IsNullOrEmpty(deviceId))
            throw new ArgumentException("Device ID cannot be null or empty", nameof(deviceId));
        
        if (key == null || key.Length != 32)
            throw new ArgumentException("Key must be exactly 32 bytes", nameof(key));

        _deviceKeys[deviceId] = key;
        
        if (persist)
        {
            await SaveDeviceKeyAsync(deviceId, key, false);
        }
        
        _logger.LogInformation("Set encryption key for device {DeviceId}", deviceId);
    }

    /// <summary>
    /// Removes encryption key for a device
    /// </summary>
    /// <param name="deviceId">Target device ID</param>
    /// <param name="deleteFromStorage">Whether to delete from persistent storage</param>
    public async Task RemoveDeviceKeyAsync(string deviceId, bool deleteFromStorage = true)
    {
        if (string.IsNullOrEmpty(deviceId))
            return;

        _deviceKeys.TryRemove(deviceId, out _);
        _keyMetadata.TryRemove(deviceId, out _);
        
        if (deleteFromStorage)
        {
            var keyFilePath = GetKeyFilePath(deviceId);
            if (File.Exists(keyFilePath))
            {
                File.Delete(keyFilePath);
                _logger.LogInformation("Deleted encryption key file for device {DeviceId}", deviceId);
            }
        }
        
        _logger.LogInformation("Removed encryption key for device {DeviceId}", deviceId);
    }

    /// <summary>
    /// Checks if we have an encryption key for a device
    /// </summary>
    /// <param name="deviceId">Target device ID</param>
    /// <returns>True if key exists</returns>
    public bool HasDeviceKey(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;

        return _deviceKeys.ContainsKey(deviceId) || File.Exists(GetKeyFilePath(deviceId));
    }

    /// <summary>
    /// Gets metadata about all stored device keys
    /// </summary>
    /// <returns>Dictionary of device IDs to key metadata</returns>
    public Dictionary<string, DeviceKeyInfo> GetDeviceKeyMetadata()
    {
        return new Dictionary<string, DeviceKeyInfo>(_keyMetadata);
    }

    private async Task<byte[]> GenerateFolderKeyAsync(string folderId, string password)
    {
        // Syncthing-style key derivation: scrypt(password, folderID as salt)
        // This ensures all devices with the same folder password generate the same key
        
        var folderIdBytes = System.Text.Encoding.UTF8.GetBytes(folderId);
        
        // Use scrypt with Syncthing-compatible parameters: 32768, 8, 1, 32
        var key = EncryptionEngine.DeriveKeyFromPassword(password, folderIdBytes, 32768);
        
        _logger.LogInformation("KeyManager: Generated Syncthing-style folder key for folder {FolderId} using scrypt", folderId);
        
        // Log key for debugging (first 8 bytes)
        var keyPreview = Convert.ToHexString(key.Take(8).ToArray());
        _logger.LogInformation("KeyManager: Generated key preview: {KeyPreview}... (first 8 bytes)", keyPreview);
        
        return key;
    }
    
    /// <summary>
    /// Legacy method for backward compatibility
    /// </summary>
    [Obsolete("Use GetOrCreateFolderKeyAsync instead")]
    public async Task<byte[]> GetOrCreateDeviceKeyAsync(string deviceId, string? password = null)
    {
        // For backward compatibility, assume deviceId is actually folderId
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password is required for folder encryption", nameof(password));
            
        return await GetOrCreateFolderKeyAsync(deviceId, password);
    }

    private async Task<byte[]?> LoadDeviceKeyAsync(string deviceId)
    {
        try
        {
            var keyFilePath = GetKeyFilePath(deviceId);
            if (!File.Exists(keyFilePath))
                return null;

            var json = await File.ReadAllTextAsync(keyFilePath);
            var keyData = JsonSerializer.Deserialize<StoredKeyData>(json);
            
            if (keyData == null)
                return null;

            // Store metadata
            _keyMetadata[deviceId] = new DeviceKeyInfo
            {
                DeviceId = deviceId,
                CreatedAt = keyData.CreatedAt,
                IsPasswordDerived = keyData.IsPasswordDerived,
                Salt = keyData.Salt,
                Iterations = keyData.Iterations
            };

            if (keyData.IsPasswordDerived)
            {
                // For password-derived keys, we need the password to reconstruct the key
                // In a real implementation, you might prompt the user or use a secure key store
                _logger.LogWarning("Cannot load password-derived key for device {DeviceId} without password", deviceId);
                return null;
            }
            else
            {
                // Decrypt the stored key
                var encryptedKey = Convert.FromBase64String(keyData.EncryptedKey);
                var key = ProtectData(encryptedKey, false); // Decrypt
                
                if (key.Length != 32)
                {
                    _logger.LogWarning("Invalid key length for device {DeviceId}: {Length}", deviceId, key.Length);
                    return null;
                }

                return key;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading encryption key for device {DeviceId}", deviceId);
            return null;
        }
    }

    private async Task SaveDeviceKeyAsync(string deviceId, byte[] key, bool isPasswordDerived)
    {
        try
        {
            var keyData = new StoredKeyData
            {
                DeviceId = deviceId,
                CreatedAt = DateTime.UtcNow,
                IsPasswordDerived = isPasswordDerived
            };

            if (isPasswordDerived && _keyMetadata.TryGetValue(deviceId, out var metadata))
            {
                keyData.Salt = metadata.Salt;
                keyData.Iterations = metadata.Iterations;
                // Don't store the actual key for password-derived keys
                keyData.EncryptedKey = string.Empty;
            }
            else
            {
                // Encrypt the key before storing
                var encryptedKey = ProtectData(key, true); // Encrypt
                keyData.EncryptedKey = Convert.ToBase64String(encryptedKey);
            }

            var json = JsonSerializer.Serialize(keyData, new JsonSerializerOptions { WriteIndented = true });
            var keyFilePath = GetKeyFilePath(deviceId);
            
            await File.WriteAllTextAsync(keyFilePath, json);
            
            _logger.LogDebug("Saved encryption key for device {DeviceId} to {Path}", deviceId, keyFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving encryption key for device {DeviceId}", deviceId);
            throw;
        }
    }

    private string GetKeyFilePath(string deviceId)
    {
        var safeDeviceId = string.Join("_", deviceId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Path.GetDirectoryName(_keyStorePath)!, $"key_{safeDeviceId}.json");
    }

    private static byte[] ProtectData(byte[] data, bool encrypt)
    {
        try
        {
            // Use DPAPI on Windows, or simple XOR obfuscation on other platforms
            if (OperatingSystem.IsWindows())
            {
                return encrypt 
                    ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
                    : ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            }
            else
            {
                // Simple XOR obfuscation (not secure, but better than plaintext)
                // In production, use proper platform-specific key storage
                var obfuscationKey = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE };
                var result = new byte[data.Length];
                
                for (int i = 0; i < data.Length; i++)
                {
                    result[i] = (byte)(data[i] ^ obfuscationKey[i % obfuscationKey.Length]);
                }
                
                return result;
            }
        }
        catch
        {
            // Fallback: return data as-is if protection fails
            return data;
        }
    }
}

/// <summary>
/// Information about a device encryption key
/// </summary>
public class DeviceKeyInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsPasswordDerived { get; set; }
    public string? Salt { get; set; }
    public int Iterations { get; set; }
}

/// <summary>
/// Data structure for storing keys persistently
/// </summary>
internal class StoredKeyData
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsPasswordDerived { get; set; }
    public string EncryptedKey { get; set; } = string.Empty;
    public string? Salt { get; set; }
    public int Iterations { get; set; }
}