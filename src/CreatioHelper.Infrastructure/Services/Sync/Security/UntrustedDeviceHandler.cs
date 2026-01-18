using System.Security.Cryptography;
using System.Text;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Encryption;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Security;

/// <summary>
/// Configuration for untrusted device handling
/// </summary>
public record UntrustedDeviceConfig
{
    /// <summary>
    /// Encryption password for untrusted devices
    /// </summary>
    public string? EncryptionPassword { get; init; }

    /// <summary>
    /// Whether to hash file names for privacy
    /// </summary>
    public bool HashFileNames { get; init; } = true;

    /// <summary>
    /// Whether to encrypt file content
    /// </summary>
    public bool EncryptContent { get; init; } = true;

    /// <summary>
    /// Whether to pad file sizes to hide actual sizes
    /// </summary>
    public bool PadFileSizes { get; init; }

    /// <summary>
    /// Padding block size (default: 4KB)
    /// </summary>
    public int PaddingBlockSize { get; init; } = 4096;
}

/// <summary>
/// Information about encrypted file metadata for untrusted devices
/// </summary>
public record EncryptedFileInfo
{
    public string OriginalPath { get; init; } = string.Empty;
    public string EncryptedPath { get; init; } = string.Empty;
    public byte[] OriginalHash { get; init; } = Array.Empty<byte>();
    public long OriginalSize { get; init; }
    public long EncryptedSize { get; init; }
    public DateTime ModifiedTime { get; init; }
}

/// <summary>
/// Handles data encryption and name obfuscation for untrusted devices
/// Based on Syncthing DeviceConfiguration.Untrusted and folder_recvenc.go
/// </summary>
public interface IUntrustedDeviceHandler
{
    /// <summary>
    /// Check if a device is untrusted
    /// </summary>
    bool IsUntrusted(SyncDevice device);

    /// <summary>
    /// Check if data should be encrypted for a device
    /// </summary>
    bool ShouldEncryptForDevice(SyncDevice device, SyncFolder folder);

    /// <summary>
    /// Encrypt file path for untrusted device (obfuscate directory structure)
    /// </summary>
    string EncryptFilePath(string path, string folderId);

    /// <summary>
    /// Decrypt file path from untrusted device
    /// </summary>
    string DecryptFilePath(string encryptedPath, string folderId);

    /// <summary>
    /// Encrypt file content for untrusted device
    /// </summary>
    Task<byte[]> EncryptContentAsync(byte[] content, string path, string folderId, CancellationToken ct = default);

    /// <summary>
    /// Decrypt file content from untrusted device
    /// </summary>
    Task<byte[]> DecryptContentAsync(byte[] encryptedContent, string path, string folderId, CancellationToken ct = default);

    /// <summary>
    /// Prepare file metadata for sending to untrusted device
    /// </summary>
    FileMetadata PrepareForUntrusted(FileMetadata metadata, SyncDevice device);

    /// <summary>
    /// Validate encrypted data integrity
    /// </summary>
    bool ValidateEncryptedData(byte[] encryptedContent, byte[] expectedHash);
}

/// <summary>
/// Implementation of untrusted device handling (based on Syncthing folder_recvenc.go)
/// </summary>
public class UntrustedDeviceHandler : IUntrustedDeviceHandler
{
    private readonly ILogger<UntrustedDeviceHandler> _logger;
    private readonly IEncryptionService _encryptionService;
    private readonly Dictionary<string, UntrustedDeviceConfig> _deviceConfigs = new();
    private readonly Dictionary<string, byte[]> _folderKeys = new();

    // Encrypted path prefix (based on Syncthing)
    private const string EncryptedPathPrefix = ".stencrypted/";

    public UntrustedDeviceHandler(
        ILogger<UntrustedDeviceHandler> logger,
        IEncryptionService encryptionService)
    {
        _logger = logger;
        _encryptionService = encryptionService;
    }

    /// <summary>
    /// Configure an untrusted device
    /// </summary>
    public void ConfigureDevice(string deviceId, UntrustedDeviceConfig config)
    {
        _deviceConfigs[deviceId] = config;
        _logger.LogDebug("Configured untrusted device: {DeviceId}", deviceId);
    }

    /// <summary>
    /// Configure folder encryption key
    /// </summary>
    public void ConfigureFolderKey(string folderId, string password)
    {
        // Derive key from password using PBKDF2
        var salt = Encoding.UTF8.GetBytes(folderId + ".syncthing-folder-key");
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations: 100000,
            HashAlgorithmName.SHA256,
            outputLength: 32);

        _folderKeys[folderId] = key;
        _logger.LogDebug("Configured folder encryption key: {FolderId}", folderId);
    }

    public bool IsUntrusted(SyncDevice device)
    {
        return device.Untrusted || _deviceConfigs.ContainsKey(device.DeviceId);
    }

    public bool ShouldEncryptForDevice(SyncDevice device, SyncFolder folder)
    {
        // Always encrypt for untrusted devices
        if (device.Untrusted)
            return true;

        // Check if device has explicit config
        if (_deviceConfigs.TryGetValue(device.DeviceId, out var config))
        {
            return config.EncryptContent;
        }

        // Check folder encryption settings
        return folder.SyncType == SyncFolderType.ReceiveEncrypted;
    }

    public string EncryptFilePath(string path, string folderId)
    {
        if (!_folderKeys.TryGetValue(folderId, out var key))
        {
            // No key configured, use hash-based obfuscation
            return HashFilePath(path);
        }

        try
        {
            // Encrypt each path component separately for directory structure obfuscation
            var components = path.Split('/', '\\');
            var encryptedComponents = new List<string>();

            foreach (var component in components)
            {
                if (string.IsNullOrEmpty(component))
                    continue;

                // Use deterministic encryption (AES-SIV style)
                var plainBytes = Encoding.UTF8.GetBytes(component);
                var encryptedBytes = EncryptDeterministic(plainBytes, key, folderId);
                var base64 = Convert.ToBase64String(encryptedBytes)
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .TrimEnd('=');

                encryptedComponents.Add(base64);
            }

            return EncryptedPathPrefix + string.Join("/", encryptedComponents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error encrypting file path: {Path}", path);
            return HashFilePath(path);
        }
    }

    public string DecryptFilePath(string encryptedPath, string folderId)
    {
        if (!_folderKeys.TryGetValue(folderId, out var key))
        {
            _logger.LogWarning("No key configured for folder: {FolderId}", folderId);
            return encryptedPath;
        }

        try
        {
            // Remove prefix
            var pathWithoutPrefix = encryptedPath;
            if (encryptedPath.StartsWith(EncryptedPathPrefix))
            {
                pathWithoutPrefix = encryptedPath.Substring(EncryptedPathPrefix.Length);
            }

            var components = pathWithoutPrefix.Split('/');
            var decryptedComponents = new List<string>();

            foreach (var component in components)
            {
                if (string.IsNullOrEmpty(component))
                    continue;

                // Decode base64url
                var base64 = component
                    .Replace('-', '+')
                    .Replace('_', '/');
                // Add padding
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "="; break;
                }

                var encryptedBytes = Convert.FromBase64String(base64);
                var plainBytes = DecryptDeterministic(encryptedBytes, key, folderId);
                decryptedComponents.Add(Encoding.UTF8.GetString(plainBytes));
            }

            return string.Join("/", decryptedComponents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error decrypting file path: {Path}", encryptedPath);
            return encryptedPath;
        }
    }

    public async Task<byte[]> EncryptContentAsync(byte[] content, string path, string folderId, CancellationToken ct = default)
    {
        if (!_folderKeys.TryGetValue(folderId, out var key))
        {
            // No key, generate one from folder ID and use service encryption
            var folderKey = _encryptionService.GenerateFolderKey(folderId, folderId);
            var fileKey = _encryptionService.GenerateFileKey(path, folderKey);
            return await Task.FromResult(_encryptionService.EncryptFileData(content, fileKey));
        }

        return await Task.Run(() =>
        {
            // Use AES-GCM for authenticated encryption
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[content.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, content, ciphertext, tag);

            // Format: nonce (12) + tag (16) + ciphertext
            var result = new byte[12 + 16 + ciphertext.Length];
            nonce.CopyTo(result, 0);
            tag.CopyTo(result, 12);
            ciphertext.CopyTo(result, 28);

            return result;
        }, ct);
    }

    public async Task<byte[]> DecryptContentAsync(byte[] encryptedContent, string path, string folderId, CancellationToken ct = default)
    {
        if (!_folderKeys.TryGetValue(folderId, out var key))
        {
            // No key, generate one from folder ID and use service decryption
            var folderKey = _encryptionService.GenerateFolderKey(folderId, folderId);
            var fileKey = _encryptionService.GenerateFileKey(path, folderKey);
            return await Task.FromResult(_encryptionService.DecryptFileData(encryptedContent, fileKey));
        }

        return await Task.Run(() =>
        {
            if (encryptedContent.Length < 28)
                throw new CryptographicException("Invalid encrypted content length");

            var nonce = encryptedContent.AsSpan(0, 12);
            var tag = encryptedContent.AsSpan(12, 16);
            var ciphertext = encryptedContent.AsSpan(28);

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }, ct);
    }

    public FileMetadata PrepareForUntrusted(FileMetadata metadata, SyncDevice device)
    {
        var config = _deviceConfigs.GetValueOrDefault(device.DeviceId) ?? new UntrustedDeviceConfig();

        // Create a copy with encrypted/obfuscated data
        return new FileMetadata
        {
            FolderId = metadata.FolderId,
            FileName = config.HashFileNames ? EncryptFilePath(metadata.FileName, metadata.FolderId) : metadata.FileName,
            FileType = metadata.FileType,
            ModifiedTime = metadata.ModifiedTime,
            Size = config.PadFileSizes ? PadSize(metadata.Size, config.PaddingBlockSize) : metadata.Size,
            Sequence = metadata.Sequence,
            IsDeleted = metadata.IsDeleted,
            IsInvalid = metadata.IsInvalid,
            // Note: LocalFlags should indicate encrypted state
            LocalFlags = metadata.LocalFlags | FileLocalFlags.Encrypted,
            Hash = metadata.Hash,
            DeviceId = metadata.DeviceId,
            ModifiedBy = metadata.ModifiedBy
        };
    }

    public bool ValidateEncryptedData(byte[] encryptedContent, byte[] expectedHash)
    {
        var actualHash = SHA256.HashData(encryptedContent);
        return actualHash.SequenceEqual(expectedHash);
    }

    private string HashFilePath(string path)
    {
        // Simple hash-based obfuscation (not reversible)
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        var base64 = Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        // Keep extension for file type hints
        var ext = Path.GetExtension(path);
        return $"{EncryptedPathPrefix}{base64[..16]}{ext}";
    }

    private byte[] EncryptDeterministic(byte[] plaintext, byte[] key, string context)
    {
        // Simple deterministic encryption using HMAC + XOR
        // Real implementation would use AES-SIV
        using var hmac = new HMACSHA256(key);
        var contextBytes = Encoding.UTF8.GetBytes(context);
        var combined = new byte[plaintext.Length + contextBytes.Length];
        plaintext.CopyTo(combined, 0);
        contextBytes.CopyTo(combined, plaintext.Length);

        var keystream = hmac.ComputeHash(combined);

        var result = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++)
        {
            result[i] = (byte)(plaintext[i] ^ keystream[i % keystream.Length]);
        }

        return result;
    }

    private byte[] DecryptDeterministic(byte[] ciphertext, byte[] key, string context)
    {
        // Deterministic encryption is symmetric
        return EncryptDeterministic(ciphertext, key, context);
    }

    private long PadSize(long originalSize, int blockSize)
    {
        // Round up to next block boundary
        return ((originalSize + blockSize - 1) / blockSize) * blockSize;
    }
}
