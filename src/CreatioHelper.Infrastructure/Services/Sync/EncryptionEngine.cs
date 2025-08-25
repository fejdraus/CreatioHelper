using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Encryption engine for block data (based on Syncthing's encryption)
/// Supports AES-256-GCM encryption for secure block transmission
/// </summary>
public class EncryptionEngine
{
    private readonly ILogger<EncryptionEngine> _logger;
    
    // Encryption constants following Syncthing specifications
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12; // 96 bits for GCM
    private const int TagSize = 16; // 128 bits authentication tag
    private const int MinEncryptionSize = 64; // Don't encrypt blocks smaller than 64 bytes

    public EncryptionEngine(ILogger<EncryptionEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Encrypts block data using AES-256-GCM
    /// </summary>
    /// <param name="data">Unencrypted data</param>
    /// <param name="key">32-byte encryption key</param>
    /// <returns>Encrypted data with nonce and tag prepended</returns>
    public (byte[] EncryptedData, bool IsEncrypted) EncryptBlock(byte[] data, byte[] key)
    {
        if (data == null || data.Length == 0)
        {
            return (data ?? Array.Empty<byte>(), false);
        }

        // SECURITY: Always encrypt all data regardless of size when encryption is enabled
        // This follows Syncthing's security-first approach

        if (key == null || key.Length != KeySize)
        {
            _logger.LogWarning("Invalid encryption key length: expected {KeySize}, got {ActualSize}", 
                KeySize, key?.Length ?? 0);
            return (data, false);
        }

        try
        {
            // Generate random nonce
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);
            
            // Prepare output buffer: nonce + encrypted_data + tag
            var encryptedData = new byte[NonceSize + data.Length + TagSize];
            var ciphertext = new byte[data.Length];
            var tag = new byte[TagSize];
            
            // Use AesGcm directly (modern approach for .NET)
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, data, ciphertext, tag);
            
            // Copy nonce, encrypted data, and tag to output buffer
            Array.Copy(nonce, 0, encryptedData, 0, NonceSize);
            Array.Copy(ciphertext, 0, encryptedData, NonceSize, ciphertext.Length);
            Array.Copy(tag, 0, encryptedData, NonceSize + ciphertext.Length, TagSize);
            
            _logger.LogTrace("Block encrypted: {OriginalSize} → {EncryptedSize} bytes (AES-256-GCM)",
                data.Length, encryptedData.Length);

            return (encryptedData, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Encryption failed, using unencrypted data");
            return (data, false);
        }
    }

    /// <summary>
    /// Decrypts block data using AES-256-GCM
    /// </summary>
    /// <param name="encryptedData">Encrypted data with nonce and tag</param>
    /// <param name="key">32-byte decryption key</param>
    /// <param name="expectedSize">Expected size after decryption (for validation)</param>
    /// <returns>Decrypted data</returns>
    public byte[] DecryptBlock(byte[] encryptedData, byte[] key, int? expectedSize = null)
    {
        if (encryptedData == null || encryptedData.Length == 0)
        {
            return encryptedData ?? Array.Empty<byte>();
        }

        if (key == null || key.Length != KeySize)
        {
            throw new ArgumentException($"Invalid encryption key length: expected {KeySize}, got {key?.Length ?? 0}");
        }

        // Check minimum encrypted data size: nonce + tag + at least 1 byte data
        var minEncryptedSize = NonceSize + TagSize + 1;
        if (encryptedData.Length < minEncryptedSize)
        {
            throw new ArgumentException($"Encrypted data too small: expected at least {minEncryptedSize}, got {encryptedData.Length}");
        }

        try
        {
            // Extract components
            var nonce = new byte[NonceSize];
            var ciphertext = new byte[encryptedData.Length - NonceSize - TagSize];
            var tag = new byte[TagSize];
            
            Array.Copy(encryptedData, 0, nonce, 0, NonceSize);
            Array.Copy(encryptedData, NonceSize, ciphertext, 0, ciphertext.Length);
            Array.Copy(encryptedData, NonceSize + ciphertext.Length, tag, 0, TagSize);
            
            // Decrypt using AES-GCM
            using var aesGcm = new AesGcm(key, TagSize);
            var plaintext = new byte[ciphertext.Length];
            
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            
            // Validate decrypted size if expected size provided
            if (expectedSize.HasValue && plaintext.Length != expectedSize.Value)
            {
                throw new InvalidDataException($"Decrypted size mismatch: expected {expectedSize.Value}, got {plaintext.Length}");
            }

            _logger.LogTrace("Block decrypted: {EncryptedSize} → {DecryptedSize} bytes (AES-256-GCM)",
                encryptedData.Length, plaintext.Length);

            return plaintext;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decryption failed for AES-256-GCM encrypted data");
            throw;
        }
    }

    /// <summary>
    /// Determines if a block should be encrypted based on its characteristics
    /// </summary>
    /// <param name="data">Block data</param>
    /// <returns>True if block should be encrypted</returns>
    public bool ShouldEncrypt(byte[] data)
    {
        // SECURITY: Always encrypt all data regardless of size when encryption is enabled
        // This ensures complete data protection following Syncthing's security model
        return data != null && data.Length > 0;
    }

    /// <summary>
    /// Generates a new 256-bit encryption key
    /// </summary>
    /// <returns>32-byte encryption key</returns>
    public static byte[] GenerateKey()
    {
        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Derives an encryption key from a password using PBKDF2
    /// </summary>
    /// <param name="password">Password string</param>
    /// <param name="salt">Salt bytes (should be at least 16 bytes)</param>
    /// <param name="iterations">Number of PBKDF2 iterations (minimum 10000 recommended)</param>
    /// <returns>32-byte encryption key</returns>
    public static byte[] DeriveKeyFromPassword(string password, byte[] salt, int iterations = 100000)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be null or empty", nameof(password));
        
        if (salt == null || salt.Length < 16)
            throw new ArgumentException("Salt must be at least 16 bytes", nameof(salt));
            
        if (iterations < 10000)
            throw new ArgumentException("Iterations must be at least 10000", nameof(iterations));

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }

    /// <summary>
    /// Generates a random salt for password derivation
    /// </summary>
    /// <param name="size">Salt size in bytes (default 32)</param>
    /// <returns>Random salt bytes</returns>
    public static byte[] GenerateSalt(int size = 32)
    {
        var salt = new byte[size];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }
}

/// <summary>
/// Encryption types supported by the engine
/// </summary>
public enum EncryptionType
{
    None = 0,        // No encryption
    AES256GCM = 1    // AES-256-GCM encryption (Syncthing's method)
}