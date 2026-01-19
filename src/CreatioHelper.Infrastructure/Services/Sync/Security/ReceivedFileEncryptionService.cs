using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Security;

/// <summary>
/// Service for managing encryption of received files.
/// Based on Syncthing's receive-encrypted folder type.
/// </summary>
public interface IReceivedFileEncryptionService
{
    /// <summary>
    /// Check if encryption is enabled for a folder.
    /// </summary>
    bool IsEncryptionEnabled(string folderId);

    /// <summary>
    /// Enable or disable encryption for a folder.
    /// </summary>
    void SetEncryptionEnabled(string folderId, bool enabled);

    /// <summary>
    /// Get encryption mode for a folder.
    /// </summary>
    EncryptionMode GetEncryptionMode(string folderId);

    /// <summary>
    /// Set encryption mode for a folder.
    /// </summary>
    void SetEncryptionMode(string folderId, EncryptionMode mode);

    /// <summary>
    /// Encrypt file data for storage.
    /// </summary>
    Task<EncryptedFileResult> EncryptFileAsync(string folderId, string filePath, Stream sourceStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypt file data for reading.
    /// </summary>
    Task<Stream> DecryptFileAsync(string folderId, string filePath, Stream encryptedStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate encrypted file name.
    /// </summary>
    string EncryptFileName(string folderId, string originalName);

    /// <summary>
    /// Decrypt file name.
    /// </summary>
    string DecryptFileName(string folderId, string encryptedName);

    /// <summary>
    /// Set encryption password for a folder.
    /// </summary>
    void SetPassword(string folderId, string password);

    /// <summary>
    /// Check if folder has a password set.
    /// </summary>
    bool HasPassword(string folderId);

    /// <summary>
    /// Get encryption statistics.
    /// </summary>
    EncryptionStats GetStats(string folderId);

    /// <summary>
    /// Verify file integrity.
    /// </summary>
    Task<bool> VerifyIntegrityAsync(string folderId, string filePath, Stream encryptedStream, CancellationToken cancellationToken = default);
}

/// <summary>
/// Encryption mode for received files.
/// </summary>
public enum EncryptionMode
{
    /// <summary>
    /// No encryption - files stored as-is.
    /// </summary>
    None,

    /// <summary>
    /// Encrypt file content only.
    /// </summary>
    ContentOnly,

    /// <summary>
    /// Encrypt file names only.
    /// </summary>
    NamesOnly,

    /// <summary>
    /// Encrypt both content and file names.
    /// </summary>
    Full
}

/// <summary>
/// Result of file encryption.
/// </summary>
public class EncryptedFileResult
{
    public bool Success { get; init; }
    public Stream? EncryptedStream { get; init; }
    public string? EncryptedFileName { get; init; }
    public byte[]? Nonce { get; init; }
    public byte[]? AuthTag { get; init; }
    public long OriginalSize { get; init; }
    public long EncryptedSize { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Encryption statistics for a folder.
/// </summary>
public class EncryptionStats
{
    public string FolderId { get; set; } = string.Empty;
    public long FilesEncrypted { get; set; }
    public long FilesDecrypted { get; set; }
    public long BytesEncrypted { get; set; }
    public long BytesDecrypted { get; set; }
    public long EncryptionErrors { get; set; }
    public long DecryptionErrors { get; set; }
    public DateTime? LastEncryption { get; set; }
    public DateTime? LastDecryption { get; set; }
    public TimeSpan TotalEncryptionTime { get; set; }
    public TimeSpan TotalDecryptionTime { get; set; }
    public double AverageEncryptionThroughput => TotalEncryptionTime.TotalSeconds > 0
        ? BytesEncrypted / TotalEncryptionTime.TotalSeconds / 1024 / 1024
        : 0; // MB/s
}

/// <summary>
/// Configuration for received file encryption.
/// </summary>
public class ReceivedFileEncryptionConfiguration
{
    /// <summary>
    /// Default encryption mode.
    /// </summary>
    public EncryptionMode DefaultMode { get; set; } = EncryptionMode.None;

    /// <summary>
    /// Key derivation iterations (PBKDF2).
    /// </summary>
    public int KeyDerivationIterations { get; set; } = 100000;

    /// <summary>
    /// Key size in bits.
    /// </summary>
    public int KeySizeBits { get; set; } = 256;

    /// <summary>
    /// Nonce size in bytes.
    /// </summary>
    public int NonceSizeBytes { get; set; } = 12;

    /// <summary>
    /// Buffer size for streaming encryption.
    /// </summary>
    public int BufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// File name obfuscation prefix.
    /// </summary>
    public string EncryptedFilePrefix { get; set; } = ".syncthing-encrypted.";
}

/// <summary>
/// Implementation of received file encryption service.
/// </summary>
public class ReceivedFileEncryptionService : IReceivedFileEncryptionService
{
    private readonly ILogger<ReceivedFileEncryptionService> _logger;
    private readonly ReceivedFileEncryptionConfiguration _config;
    private readonly ConcurrentDictionary<string, EncryptionMode> _folderModes = new();
    private readonly ConcurrentDictionary<string, byte[]> _folderKeys = new();
    private readonly ConcurrentDictionary<string, EncryptionStats> _stats = new();

    public ReceivedFileEncryptionService(
        ILogger<ReceivedFileEncryptionService> logger,
        ReceivedFileEncryptionConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ReceivedFileEncryptionConfiguration();
    }

    /// <inheritdoc />
    public bool IsEncryptionEnabled(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        return _folderModes.TryGetValue(folderId, out var mode) && mode != EncryptionMode.None;
    }

    /// <inheritdoc />
    public void SetEncryptionEnabled(string folderId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (enabled)
        {
            _folderModes[folderId] = _config.DefaultMode != EncryptionMode.None
                ? _config.DefaultMode
                : EncryptionMode.Full;
        }
        else
        {
            _folderModes[folderId] = EncryptionMode.None;
        }

        _logger.LogInformation("Encryption {State} for folder {FolderId}",
            enabled ? "enabled" : "disabled", folderId);
    }

    /// <inheritdoc />
    public EncryptionMode GetEncryptionMode(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        return _folderModes.GetValueOrDefault(folderId, _config.DefaultMode);
    }

    /// <inheritdoc />
    public void SetEncryptionMode(string folderId, EncryptionMode mode)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        _folderModes[folderId] = mode;

        _logger.LogInformation("Set encryption mode {Mode} for folder {FolderId}", mode, folderId);
    }

    /// <inheritdoc />
    public async Task<EncryptedFileResult> EncryptFileAsync(
        string folderId,
        string filePath,
        Stream sourceStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(sourceStream);

        var mode = GetEncryptionMode(folderId);
        if (mode == EncryptionMode.None || mode == EncryptionMode.NamesOnly)
        {
            // No content encryption needed
            return new EncryptedFileResult
            {
                Success = true,
                EncryptedStream = sourceStream,
                EncryptedFileName = mode == EncryptionMode.NamesOnly ? EncryptFileName(folderId, filePath) : filePath,
                OriginalSize = sourceStream.Length,
                EncryptedSize = sourceStream.Length
            };
        }

        if (!_folderKeys.TryGetValue(folderId, out var key))
        {
            return new EncryptedFileResult
            {
                Success = false,
                ErrorMessage = "No encryption key set for folder"
            };
        }

        var startTime = DateTime.UtcNow;
        var stats = GetOrCreateStats(folderId);

        try
        {
            var nonce = RandomNumberGenerator.GetBytes(_config.NonceSizeBytes);
            var outputStream = new MemoryStream();

            using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);

            // Read all data (for AES-GCM we need to process all at once)
            var plaintext = new byte[sourceStream.Length];
            await sourceStream.ReadExactlyAsync(plaintext, cancellationToken);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];

            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Write nonce + ciphertext + tag
            await outputStream.WriteAsync(nonce, cancellationToken);
            await outputStream.WriteAsync(ciphertext, cancellationToken);
            await outputStream.WriteAsync(tag, cancellationToken);

            outputStream.Position = 0;

            lock (stats)
            {
                stats.FilesEncrypted++;
                stats.BytesEncrypted += plaintext.Length;
                stats.LastEncryption = DateTime.UtcNow;
                stats.TotalEncryptionTime += DateTime.UtcNow - startTime;
            }

            _logger.LogDebug("Encrypted file {FilePath} ({Size} bytes) for folder {FolderId}",
                filePath, plaintext.Length, folderId);

            return new EncryptedFileResult
            {
                Success = true,
                EncryptedStream = outputStream,
                EncryptedFileName = mode == EncryptionMode.Full ? EncryptFileName(folderId, filePath) : filePath,
                Nonce = nonce,
                AuthTag = tag,
                OriginalSize = plaintext.Length,
                EncryptedSize = outputStream.Length
            };
        }
        catch (Exception ex)
        {
            lock (stats)
            {
                stats.EncryptionErrors++;
            }

            _logger.LogError(ex, "Failed to encrypt file {FilePath} for folder {FolderId}", filePath, folderId);

            return new EncryptedFileResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<Stream> DecryptFileAsync(
        string folderId,
        string filePath,
        Stream encryptedStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(encryptedStream);

        var mode = GetEncryptionMode(folderId);
        if (mode == EncryptionMode.None || mode == EncryptionMode.NamesOnly)
        {
            return encryptedStream;
        }

        if (!_folderKeys.TryGetValue(folderId, out var key))
        {
            throw new InvalidOperationException("No encryption key set for folder");
        }

        var startTime = DateTime.UtcNow;
        var stats = GetOrCreateStats(folderId);

        try
        {
            // Read nonce
            var nonce = new byte[_config.NonceSizeBytes];
            await encryptedStream.ReadExactlyAsync(nonce, cancellationToken);

            // Read ciphertext (everything except nonce and tag)
            var tagSize = AesGcm.TagByteSizes.MaxSize;
            var ciphertextLength = (int)(encryptedStream.Length - encryptedStream.Position - tagSize);
            var ciphertext = new byte[ciphertextLength];
            await encryptedStream.ReadExactlyAsync(ciphertext, cancellationToken);

            // Read tag
            var tag = new byte[tagSize];
            await encryptedStream.ReadExactlyAsync(tag, cancellationToken);

            using var aes = new AesGcm(key, tagSize);

            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var outputStream = new MemoryStream(plaintext);

            lock (stats)
            {
                stats.FilesDecrypted++;
                stats.BytesDecrypted += plaintext.Length;
                stats.LastDecryption = DateTime.UtcNow;
                stats.TotalDecryptionTime += DateTime.UtcNow - startTime;
            }

            _logger.LogDebug("Decrypted file {FilePath} ({Size} bytes) for folder {FolderId}",
                filePath, plaintext.Length, folderId);

            return outputStream;
        }
        catch (Exception ex)
        {
            lock (stats)
            {
                stats.DecryptionErrors++;
            }

            _logger.LogError(ex, "Failed to decrypt file {FilePath} for folder {FolderId}", filePath, folderId);
            throw;
        }
    }

    /// <inheritdoc />
    public string EncryptFileName(string folderId, string originalName)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(originalName);

        var mode = GetEncryptionMode(folderId);
        if (mode == EncryptionMode.None || mode == EncryptionMode.ContentOnly)
        {
            return originalName;
        }

        if (!_folderKeys.TryGetValue(folderId, out var key))
        {
            return originalName;
        }

        // Use HMAC-SHA256 to generate deterministic encrypted name
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(originalName));
        var encoded = Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        return _config.EncryptedFilePrefix + encoded;
    }

    /// <inheritdoc />
    public string DecryptFileName(string folderId, string encryptedName)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(encryptedName);

        // Note: HMAC-based encryption is one-way, so we can't truly decrypt
        // In practice, we'd need a mapping table or different encryption approach
        // This is a simplified implementation

        if (!encryptedName.StartsWith(_config.EncryptedFilePrefix))
        {
            return encryptedName;
        }

        // For real implementation, we'd need to maintain a mapping
        _logger.LogWarning("File name decryption requires lookup table (not implemented)");
        return encryptedName;
    }

    /// <inheritdoc />
    public void SetPassword(string folderId, string password)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(password);

        // Derive key from password using PBKDF2
        var salt = System.Text.Encoding.UTF8.GetBytes(folderId); // Use folderId as salt
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            _config.KeyDerivationIterations,
            HashAlgorithmName.SHA256,
            _config.KeySizeBits / 8);
        _folderKeys[folderId] = key;

        _logger.LogInformation("Set encryption password for folder {FolderId}", folderId);
    }

    /// <inheritdoc />
    public bool HasPassword(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        return _folderKeys.ContainsKey(folderId);
    }

    /// <inheritdoc />
    public EncryptionStats GetStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        return _stats.GetOrAdd(folderId, id => new EncryptionStats { FolderId = id });
    }

    /// <inheritdoc />
    public async Task<bool> VerifyIntegrityAsync(
        string folderId,
        string filePath,
        Stream encryptedStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(encryptedStream);

        try
        {
            var position = encryptedStream.Position;
            await DecryptFileAsync(folderId, filePath, encryptedStream, cancellationToken);
            encryptedStream.Position = position;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private EncryptionStats GetOrCreateStats(string folderId)
    {
        return _stats.GetOrAdd(folderId, id => new EncryptionStats { FolderId = id });
    }
}
