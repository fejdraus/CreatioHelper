using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Encryption;

/// <summary>
/// Syncthing-compatible encryption implementation using ChaCha20-Poly1305 and AES-SIV
/// </summary>
public class SyncthingEncryption : IDisposable
{
    private readonly ILogger<SyncthingEncryption> _logger;
    private readonly RandomNumberGenerator _rng;
    private bool _disposed = false;

    // Constants matching Syncthing
    private const int NonceSize = 24;  // ChaCha20Poly1305 XChaCha20 nonce size
    private const int TagSize = 16;    // ChaCha20Poly1305 authentication tag size
    private const int KeySize = 32;    // 256-bit keys
    private const int MinPaddedSize = 1024; // Minimum padded block size
    public const int BlockOverhead = TagSize + NonceSize;

    public SyncthingEncryption(ILogger<SyncthingEncryption> logger)
    {
        _logger = logger;
        _rng = RandomNumberGenerator.Create();
        
        _logger.LogDebug("SyncthingEncryption initialized");
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
    /// Encrypts data with deterministic result using AES-SIV (placeholder)
    /// </summary>
    public byte[] EncryptDeterministic(byte[] data, byte[] key, byte[]? additionalData = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingEncryption));
        if (key.Length != KeySize) throw new ArgumentException("Invalid key size");

        // TODO: Implement proper AES-SIV encryption
        // For now, use HMAC-based deterministic encryption as placeholder
        using var hmac = new HMACSHA256(key);
        
        var inputData = data;
        if (additionalData != null)
        {
            inputData = new byte[data.Length + additionalData.Length];
            Buffer.BlockCopy(data, 0, inputData, 0, data.Length);
            Buffer.BlockCopy(additionalData, 0, inputData, data.Length, additionalData.Length);
        }
        
        var hash = hmac.ComputeHash(inputData);
        
        // Create deterministic "ciphertext" by XORing with key-derived data
        var result = new byte[data.Length + 16]; // +16 for "authentication tag"
        
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ hash[i % hash.Length]);
        }
        
        // Append authentication tag
        Buffer.BlockCopy(hash, 0, result, data.Length, 16);
        
        return result;
    }

    /// <summary>
    /// Decrypts deterministic encryption using AES-SIV (placeholder)
    /// </summary>
    public byte[] DecryptDeterministic(byte[] encryptedData, byte[] key, byte[]? additionalData = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingEncryption));
        if (key.Length != KeySize) throw new ArgumentException("Invalid key size");
        if (encryptedData.Length < 16) throw new ArgumentException("Data too short");

        try
        {
            // TODO: Implement proper AES-SIV decryption
            // For now, reverse the HMAC-based encryption
            var dataLength = encryptedData.Length - 16;
            var ciphertext = new byte[dataLength];
            var tag = new byte[16];
            
            Buffer.BlockCopy(encryptedData, 0, ciphertext, 0, dataLength);
            Buffer.BlockCopy(encryptedData, dataLength, tag, 0, 16);
            
            using var hmac = new HMACSHA256(key);
            var plaintext = new byte[dataLength];
            
            // First pass - decrypt to get plaintext
            var hash = hmac.ComputeHash(key); // Initial hash for XOR
            for (int i = 0; i < dataLength; i++)
            {
                plaintext[i] = (byte)(ciphertext[i] ^ hash[i % hash.Length]);
            }
            
            // Verify authentication by recomputing hash
            var verifyData = plaintext;
            if (additionalData != null)
            {
                verifyData = new byte[plaintext.Length + additionalData.Length];
                Buffer.BlockCopy(plaintext, 0, verifyData, 0, plaintext.Length);
                Buffer.BlockCopy(additionalData, 0, verifyData, plaintext.Length, additionalData.Length);
            }
            
            var expectedHash = hmac.ComputeHash(verifyData);
            
            // Compare authentication tags
            bool valid = true;
            for (int i = 0; i < 16; i++)
            {
                if (tag[i] != expectedHash[i])
                {
                    valid = false;
                    break;
                }
            }
            
            if (!valid)
            {
                throw new CryptographicException("Authentication tag mismatch");
            }
            
            return plaintext;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt deterministic data");
            throw new CryptographicException("Deterministic decryption failed", ex);
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