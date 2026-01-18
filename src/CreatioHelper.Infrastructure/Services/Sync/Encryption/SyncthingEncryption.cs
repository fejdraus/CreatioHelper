using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Encryption;

/// <summary>
/// Syncthing-compatible encryption implementation using ChaCha20-Poly1305 and AES-SIV.
/// - ChaCha20-Poly1305: Used for file content encryption (randomized)
/// - AES-SIV (RFC 5297): Used for filename encryption (deterministic)
/// </summary>
public class SyncthingEncryption : IDisposable
{
    private readonly ILogger<SyncthingEncryption> _logger;
    private readonly RandomNumberGenerator _rng;
    private readonly AesSivCipher _aesSiv;
    private bool _disposed = false;

    // Constants matching Syncthing
    private const int NonceSize = 24;  // ChaCha20Poly1305 XChaCha20 nonce size
    private const int TagSize = 16;    // ChaCha20Poly1305 authentication tag size
    private const int KeySize = 32;    // 256-bit keys
    private const int MinPaddedSize = 1024; // Minimum padded block size
    public const int BlockOverhead = TagSize + NonceSize;

    /// <summary>
    /// AES-SIV tag size (16 bytes).
    /// </summary>
    public const int SivTagSize = AesSivCipher.SivTagSize;

    public SyncthingEncryption(ILogger<SyncthingEncryption> logger)
    {
        _logger = logger;
        _rng = RandomNumberGenerator.Create();
        _aesSiv = new AesSivCipher();

        _logger.LogDebug("SyncthingEncryption initialized with AES-SIV support");
    }

    /// <summary>
    /// Encrypts data with a random nonce using ChaCha20-Poly1305
    /// </summary>
    public byte[] EncryptBytes(byte[] data, byte[] key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingEncryption));
        if (key.Length != KeySize) throw new ArgumentException("Invalid key size");
        
        var nonce = GenerateRandomNonce();
        return Encrypt(data, nonce, key);
    }

    /// <summary>
    /// Decrypts data using ChaCha20-Poly1305
    /// </summary>
    public byte[] DecryptBytes(byte[] encryptedData, byte[] key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingEncryption));
        if (key.Length != KeySize) throw new ArgumentException("Invalid key size");
        if (encryptedData.Length < BlockOverhead) throw new ArgumentException("Data too short");

        try
        {
            // Extract nonce from the beginning of encrypted data
            var nonce = new byte[NonceSize];
            Buffer.BlockCopy(encryptedData, 0, nonce, 0, NonceSize);
            
            // Extract ciphertext + tag
            var ciphertext = new byte[encryptedData.Length - NonceSize];
            Buffer.BlockCopy(encryptedData, NonceSize, ciphertext, 0, ciphertext.Length);

            // Decrypt using ChaCha20-Poly1305
            using var aead = new ChaCha20Poly1305(key);
            var plaintext = new byte[ciphertext.Length - TagSize];
            
            aead.Decrypt(nonce, ciphertext, new byte[0], plaintext);
            
            return plaintext;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt data block");
            throw new CryptographicException("Decryption failed", ex);
        }
    }

    /// <summary>
    /// Encrypts data with deterministic result using AES-SIV (RFC 5297).
    /// The same plaintext and key always produce the same ciphertext.
    /// This is used for filename encryption in Syncthing encrypted folders.
    /// </summary>
    /// <param name="data">The plaintext data to encrypt.</param>
    /// <param name="key">The 256-bit encryption key.</param>
    /// <param name="associatedData">Optional associated data (authenticated but not encrypted).</param>
    /// <returns>The ciphertext with prepended SIV tag.</returns>
    public byte[] EncryptDeterministic(byte[] data, byte[] key, byte[]? associatedData = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingEncryption));
        if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));

        try
        {
            if (associatedData != null)
            {
                return _aesSiv.Encrypt(data, key, associatedData);
            }
            else
            {
                return _aesSiv.Encrypt(data, key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AES-SIV encryption failed");
            throw new CryptographicException("AES-SIV encryption failed", ex);
        }
    }

    /// <summary>
    /// Decrypts data encrypted with AES-SIV (RFC 5297).
    /// </summary>
    /// <param name="encryptedData">The ciphertext with prepended SIV tag.</param>
    /// <param name="key">The 256-bit encryption key.</param>
    /// <param name="associatedData">Optional associated data (must match encryption).</param>
    /// <returns>The decrypted plaintext.</returns>
    public byte[] DecryptDeterministic(byte[] encryptedData, byte[] key, byte[]? associatedData = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingEncryption));
        if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));
        if (encryptedData.Length < SivTagSize) throw new ArgumentException("Data too short for AES-SIV", nameof(encryptedData));

        try
        {
            if (associatedData != null)
            {
                return _aesSiv.Decrypt(encryptedData, key, associatedData);
            }
            else
            {
                return _aesSiv.Decrypt(encryptedData, key);
            }
        }
        catch (CryptographicException)
        {
            throw; // Re-throw authentication failures
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AES-SIV decryption failed");
            throw new CryptographicException("AES-SIV decryption failed", ex);
        }
    }

    /// <summary>
    /// Encrypts filename deterministically for filesystem storage
    /// </summary>
    public string EncryptFilename(string filename, byte[] folderKey)
    {
        var filenameBytes = Encoding.UTF8.GetBytes(filename);
        var encrypted = EncryptDeterministic(filenameBytes, folderKey);
        var base32 = Convert.ToHexString(encrypted).ToLower();
        
        // Add Syncthing-style directory structure
        return CreateEncryptedPath(base32);
    }

    /// <summary>
    /// Decrypts filename from encrypted path
    /// </summary>
    public string DecryptFilename(string encryptedPath, byte[] folderKey)
    {
        var hex = ExtractFromEncryptedPath(encryptedPath);
        var encryptedBytes = Convert.FromHexString(hex);
        var filenameBytes = DecryptDeterministic(encryptedBytes, folderKey);
        
        return Encoding.UTF8.GetString(filenameBytes);
    }

    /// <summary>
    /// Pads data to minimum block size if needed
    /// </summary>
    public byte[] PadData(byte[] data)
    {
        if (data.Length >= MinPaddedSize) return data;
        
        var padded = new byte[MinPaddedSize];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        
        // Fill padding with random data
        var padding = new byte[MinPaddedSize - data.Length];
        _rng.GetBytes(padding);
        Buffer.BlockCopy(padding, 0, padded, data.Length, padding.Length);
        
        return padded;
    }

    /// <summary>
    /// Internal method to encrypt with specific nonce
    /// </summary>
    private byte[] Encrypt(byte[] data, byte[] nonce, byte[] key)
    {
        if (nonce.Length != NonceSize) throw new ArgumentException("Invalid nonce size");
        
        using var aead = new ChaCha20Poly1305(key);
        var ciphertext = new byte[data.Length + TagSize];
        
        aead.Encrypt(nonce, data, ciphertext, new byte[0]);
        
        // Prepend nonce to ciphertext
        var result = new byte[NonceSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
        
        return result;
    }

    /// <summary>
    /// Generates cryptographically random nonce
    /// </summary>
    private byte[] GenerateRandomNonce()
    {
        var nonce = new byte[NonceSize];
        _rng.GetBytes(nonce);
        return nonce;
    }

    /// <summary>
    /// Creates Syncthing-style encrypted path structure
    /// </summary>
    private string CreateEncryptedPath(string base32)
    {
        if (base32.Length == 0) return "";
        
        // Syncthing format: A.syncthing-enc/BC/DEFGH...
        var components = new List<string>();
        components.Add(base32[0] + ".syncthing-enc");
        
        if (base32.Length > 1)
        {
            components.Add(base32.Substring(1, Math.Min(2, base32.Length - 1)));
        }
        
        int remaining = 3;
        while (remaining < base32.Length)
        {
            int chunkSize = Math.Min(200, base32.Length - remaining);
            components.Add(base32.Substring(remaining, chunkSize));
            remaining += chunkSize;
        }
        
        return string.Join("/", components);
    }

    /// <summary>
    /// Extracts base32 string from encrypted path
    /// </summary>
    private string ExtractFromEncryptedPath(string encryptedPath)
    {
        var parts = encryptedPath.Split('/');
        if (parts.Length == 0) return "";
        
        var result = new StringBuilder();
        
        // Extract first character from A.syncthing-enc
        if (parts[0].EndsWith(".syncthing-enc"))
        {
            result.Append(parts[0][0]);
        }
        
        // Add remaining parts
        for (int i = 1; i < parts.Length; i++)
        {
            result.Append(parts[i]);
        }
        
        return result.ToString();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _rng?.Dispose();
            _disposed = true;
            _logger.LogDebug("SyncthingEncryption disposed");
        }
    }
}