using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CreatioHelper.Infrastructure.Services.Sync.Encryption;

/// <summary>
/// Simplified encryption service that provides basic Syncthing-compatible encryption
/// </summary>
public class SimplifiedEncryptionService : IEncryptionService, IDisposable
{
    private readonly ILogger<SimplifiedEncryptionService> _logger;
    private readonly EncryptionKeyGenerator _keyGenerator;
    private readonly SyncthingEncryption _encryption;
    private readonly ConcurrentDictionary<string, EncryptionInfo> _folderEncryption;
    private bool _disposed = false;

    public SimplifiedEncryptionService(
        ILogger<SimplifiedEncryptionService> logger,
        EncryptionKeyGenerator keyGenerator,
        SyncthingEncryption encryption)
    {
        _logger = logger;
        _keyGenerator = keyGenerator;
        _encryption = encryption;
        _folderEncryption = new ConcurrentDictionary<string, EncryptionInfo>();
        
        _logger.LogInformation("SimplifiedEncryptionService initialized");
    }

    public byte[] GenerateFolderKey(string folderId, string password)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            var key = _keyGenerator.KeyFromPassword(folderId, password);
            _logger.LogDebug("Generated folder key for {FolderId}", folderId);
            return key;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate folder key for {FolderId}", folderId);
            throw;
        }
    }

    public byte[] GenerateFileKey(string filename, byte[] folderKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            var key = _keyGenerator.FileKey(filename, folderKey);
            _logger.LogDebug("Generated file key for {Filename}", filename);
            return key;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate file key for {Filename}", filename);
            throw;
        }
    }

    public byte[] EncryptFileData(byte[] data, byte[] fileKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            // Pad data if needed for small blocks
            var paddedData = _encryption.PadData(data);
            var encrypted = _encryption.EncryptBytes(paddedData, fileKey);
            
            _logger.LogDebug("Encrypted file data: {OriginalSize} -> {EncryptedSize} bytes", 
                data.Length, encrypted.Length);
            return encrypted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt file data");
            throw;
        }
    }

    public byte[] DecryptFileData(byte[] encryptedData, byte[] fileKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            var decrypted = _encryption.DecryptBytes(encryptedData, fileKey);
            _logger.LogDebug("Decrypted file data: {EncryptedSize} -> {DecryptedSize} bytes", 
                encryptedData.Length, decrypted.Length);
            return decrypted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt file data");
            throw;
        }
    }

    public SyncFileInfo EncryptFileInfo(SyncFileInfo fileInfo, byte[] folderKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            // For simplicity, return a placeholder encrypted file info
            // In a full implementation, this would serialize and encrypt the entire fileInfo
            _logger.LogDebug("Encrypted FileInfo for {FileName} (placeholder implementation)", fileInfo.Name);
            
            // Return the original for now - this is a simplified implementation
            return fileInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt FileInfo for {FileName}", fileInfo.Name);
            throw;
        }
    }

    public SyncFileInfo DecryptFileInfo(SyncFileInfo encryptedFileInfo, byte[] folderKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            _logger.LogDebug("Decrypted FileInfo (placeholder implementation)");
            
            // Return the original for now - this is a simplified implementation
            return encryptedFileInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt FileInfo");
            throw;
        }
    }

    public string EncryptFilename(string filename, byte[] folderKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            return _encryption.EncryptFilename(filename, folderKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt filename {Filename}", filename);
            throw;
        }
    }

    public string DecryptFilename(string encryptedFilename, byte[] folderKey)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            return _encryption.DecryptFilename(encryptedFilename, folderKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt filename");
            throw;
        }
    }

    public byte[] GeneratePasswordToken(string folderId, string password)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            var token = _keyGenerator.PasswordToken(folderId, password);
            _logger.LogDebug("Generated password token for folder {FolderId}", folderId);
            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate password token for {FolderId}", folderId);
            throw;
        }
    }

    public bool VerifyPasswordToken(string folderId, string password, byte[] token)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            var expectedToken = GeneratePasswordToken(folderId, password);
            
            if (expectedToken.Length != token.Length)
            {
                return false;
            }
            
            // Constant-time comparison
            bool isValid = true;
            for (int i = 0; i < expectedToken.Length; i++)
            {
                if (expectedToken[i] != token[i])
                {
                    isValid = false;
                }
            }
            
            _logger.LogDebug("Password token verification for {FolderId}: {Result}", folderId, isValid);
            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify password token for {FolderId}", folderId);
            return false;
        }
    }

    public bool IsFolderEncrypted(string folderId)
    {
        return _folderEncryption.TryGetValue(folderId, out var info) && info.IsEnabled;
    }

    public void SetFolderPassword(string folderId, string password)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        try
        {
            var token = GeneratePasswordToken(folderId, password);
            
            var encryptionInfo = new EncryptionInfo
            {
                FolderId = folderId,
                IsEnabled = true,
                PasswordToken = token,
                LastUpdated = DateTime.UtcNow
            };
            
            _folderEncryption.AddOrUpdate(folderId, encryptionInfo, (_, _) => encryptionInfo);
            
            _logger.LogInformation("Set encryption password for folder {FolderId}", folderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set folder password for {FolderId}", folderId);
            throw;
        }
    }

    public void RemoveFolderEncryption(string folderId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimplifiedEncryptionService));
        
        _folderEncryption.TryRemove(folderId, out _);
        _logger.LogInformation("Removed encryption for folder {FolderId}", folderId);
    }

    public EncryptionInfo? GetFolderEncryptionInfo(string folderId)
    {
        _folderEncryption.TryGetValue(folderId, out var info);
        return info;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _keyGenerator?.Dispose();
            _encryption?.Dispose();
            _folderEncryption?.Clear();
            _disposed = true;
            _logger.LogDebug("SimplifiedEncryptionService disposed");
        }
    }
}