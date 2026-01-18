using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace CreatioHelper.Infrastructure.Services.Sync.Encryption;

/// <summary>
/// Key generator compatible with Syncthing's encryption scheme.
/// Uses Scrypt for folder keys, HKDF for file keys, and AES-SIV for tokens.
/// </summary>
public class EncryptionKeyGenerator : IDisposable
{
    private readonly ILogger<EncryptionKeyGenerator> _logger;
    private readonly AesSivCipher _aesSiv;
    private readonly Dictionary<string, byte[]> _folderKeyCache;
    private readonly Dictionary<string, byte[]> _fileKeyCache;
    private readonly object _lock = new();
    private bool _disposed = false;

    // Constants matching Syncthing exactly
    private const int KeySize = 32;
    private const int ScryptN = 32768;  // 2^15 - Syncthing default
    private const int ScryptR = 8;
    private const int ScryptP = 1;
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("syncthing");

    public EncryptionKeyGenerator(ILogger<EncryptionKeyGenerator> logger)
    {
        _logger = logger;
        _aesSiv = new AesSivCipher();
        _folderKeyCache = new Dictionary<string, byte[]>();
        _fileKeyCache = new Dictionary<string, byte[]>();

        _logger.LogDebug("EncryptionKeyGenerator initialized with AES-SIV cipher");
    }

    /// <summary>
    /// Derives a folder key from password using Scrypt (Syncthing compatible).
    /// Matches Syncthing's keyFromPassword function in lib/protocol/encryption.go
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
            // Salt = "syncthing" + folderId
            var salt = GetKnownBytes(folderId);
            var passwordBytes = Encoding.UTF8.GetBytes(password);

            // Use Scrypt with Syncthing's exact parameters: N=32768, r=8, p=1
            var key = SCrypt.Generate(passwordBytes, salt, ScryptN, ScryptR, ScryptP, KeySize);

            // Cache the key
            _folderKeyCache[cacheKey] = key;

            _logger.LogDebug("Generated folder key for folder {FolderId} using Scrypt", folderId);
            return key;
        }
    }

    /// <summary>
    /// Derives a file-specific key from folder key using HKDF (Syncthing compatible).
    /// Matches Syncthing's FileKey function in lib/protocol/encryption.go
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

            // HKDF-SHA256 with:
            // - IKM (Input Key Material): folder key
            // - Salt: filename bytes
            // - Info: "syncthing"
            var filenameBytes = Encoding.UTF8.GetBytes(filename);

            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(folderKey, filenameBytes, HkdfInfo));

            var fileKey = new byte[KeySize];
            hkdf.GenerateBytes(fileKey, 0, KeySize);

            // Cache the key
            _fileKeyCache[cacheKey] = fileKey;

            _logger.LogDebug("Generated file key for {Filename} using HKDF", filename);
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
    /// AES-SIV deterministic encryption using RFC 5297.
    /// Provides authenticated encryption that is nonce-misuse resistant.
    /// </summary>
    private byte[] AesSivEncrypt(byte[] plaintext, byte[] key, byte[]? additionalData)
    {
        if (additionalData != null)
        {
            return _aesSiv.Encrypt(plaintext, key, additionalData);
        }
        return _aesSiv.Encrypt(plaintext, key);
    }

    /// <summary>
    /// AES-SIV deterministic decryption using RFC 5297.
    /// </summary>
    private byte[] AesSivDecrypt(byte[] ciphertext, byte[] key, byte[]? additionalData)
    {
        if (additionalData != null)
        {
            return _aesSiv.Decrypt(ciphertext, key, additionalData);
        }
        return _aesSiv.Decrypt(ciphertext, key);
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