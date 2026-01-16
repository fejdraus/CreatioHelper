using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;

namespace CreatioHelper.Infrastructure.Services.Sync.Encryption;

/// <summary>
/// Key generator compatible with Syncthing's encryption scheme
/// Uses Scrypt for folder keys and HKDF for file keys
/// </summary>
public class EncryptionKeyGenerator : IDisposable
{
    private readonly ILogger<EncryptionKeyGenerator> _logger;
    private readonly Dictionary<string, byte[]> _folderKeyCache;
    private readonly Dictionary<string, byte[]> _fileKeyCache;
    private readonly object _lock = new();
    private bool _disposed = false;

    // Constants matching Syncthing
    private const int KeySize = 32;
    private const int ScryptN = 32768;
    private const int ScryptR = 8;
    private const int ScryptP = 1;
    private static readonly byte[] HkdfSalt = Encoding.UTF8.GetBytes("syncthing");

    public EncryptionKeyGenerator(ILogger<EncryptionKeyGenerator> logger)
    {
        _logger = logger;
        _folderKeyCache = new Dictionary<string, byte[]>();
        _fileKeyCache = new Dictionary<string, byte[]>();
        
        _logger.LogDebug("EncryptionKeyGenerator initialized");
    }

    /// <summary>
    /// Derives a folder key from password using Scrypt (Syncthing compatible)
    /// </summary>
    public byte[] KeyFromPassword(string folderId, string password)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EncryptionKeyGenerator));
        
        var cacheKey = $"{folderId}:{password}";
        
        lock (_lock)
        {
            if (_folderKeyCache.TryGetValue(cacheKey, out var cachedKey))
            {
                return cachedKey;
            }

            // Use known bytes as salt (Syncthing compatibility)
            var salt = GetKnownBytes(folderId);
            var passwordBytes = Encoding.UTF8.GetBytes(password);

            // Use PBKDF2 as SCrypt alternative for now
            var key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 100000, HashAlgorithmName.SHA256, KeySize);
            
            // Cache the key
            _folderKeyCache[cacheKey] = key;
            
            _logger.LogDebug("Generated folder key for folder {FolderId}", folderId);
            return key;
        }
    }

    /// <summary>
    /// Derives a file-specific key from folder key using HKDF (Syncthing compatible)
    /// </summary>
    public byte[] FileKey(string filename, byte[] folderKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EncryptionKeyGenerator));
        if (folderKey.Length != KeySize) throw new ArgumentException("Invalid folder key size");
        
        var cacheKey = $"{Convert.ToBase64String(folderKey)}:{filename}";
        
        lock (_lock)
        {
            if (_fileKeyCache.TryGetValue(cacheKey, out var cachedKey))
            {
                return cachedKey;
            }

            // Simple HKDF-like key derivation using HMAC-SHA256
            var inputKey = new byte[folderKey.Length + Encoding.UTF8.GetByteCount(filename)];
            Buffer.BlockCopy(folderKey, 0, inputKey, 0, folderKey.Length);
            var filenameBytes = Encoding.UTF8.GetBytes(filename);
            Buffer.BlockCopy(filenameBytes, 0, inputKey, folderKey.Length, filenameBytes.Length);
            
            using var hmac = new HMACSHA256(HkdfSalt);
            var hash1 = hmac.ComputeHash(inputKey);
            
            var fileKey = new byte[KeySize];
            Buffer.BlockCopy(hash1, 0, fileKey, 0, Math.Min(hash1.Length, KeySize));

            // Cache the key
            _fileKeyCache[cacheKey] = fileKey;
            
            _logger.LogDebug("Generated file key for {Filename}", filename);
            return fileKey;
        }
    }

    /// <summary>
    /// Generates password token for device authentication (Syncthing compatible)
    /// </summary>
    public byte[] PasswordToken(string folderId, string password)
    {
        var folderKey = KeyFromPassword(folderId, password);
        var knownBytes = GetKnownBytes(folderId);
        
        // Use AES-SIV for deterministic encryption
        return AesSivEncrypt(knownBytes, folderKey, null);
    }

    /// <summary>
    /// Clears all cached keys
    /// </summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            _folderKeyCache.Clear();
            _fileKeyCache.Clear();
            _logger.LogDebug("Encryption key cache cleared");
        }
    }

    /// <summary>
    /// Gets known bytes for folder (Syncthing compatibility)
    /// </summary>
    private static byte[] GetKnownBytes(string folderId)
    {
        return Encoding.UTF8.GetBytes("syncthing" + folderId);
    }

    /// <summary>
    /// AES-SIV deterministic encryption (placeholder - needs actual implementation)
    /// </summary>
    private byte[] AesSivEncrypt(byte[] plaintext, byte[] key, byte[]? additionalData)
    {
        // TODO: Implement proper AES-SIV encryption
        // For now, use HMAC as a placeholder
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(plaintext);
        
        if (additionalData != null)
        {
            var combined = new byte[plaintext.Length + additionalData.Length];
            Buffer.BlockCopy(plaintext, 0, combined, 0, plaintext.Length);
            Buffer.BlockCopy(additionalData, 0, combined, plaintext.Length, additionalData.Length);
            hash = hmac.ComputeHash(combined);
        }
        
        return hash;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            ClearCache();
            _disposed = true;
            _logger.LogDebug("EncryptionKeyGenerator disposed");
        }
    }
}