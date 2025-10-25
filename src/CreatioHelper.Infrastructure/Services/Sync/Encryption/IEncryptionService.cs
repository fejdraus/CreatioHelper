using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Sync.Encryption;

/// <summary>
/// Interface for Syncthing-compatible encryption services
/// </summary>
public interface IEncryptionService
{
    /// <summary>
    /// Generates folder key from password
    /// </summary>
    byte[] GenerateFolderKey(string folderId, string password);

    /// <summary>
    /// Generates file-specific key from folder key
    /// </summary>
    byte[] GenerateFileKey(string filename, byte[] folderKey);

    /// <summary>
    /// Encrypts file data for storage/transmission
    /// </summary>
    byte[] EncryptFileData(byte[] data, byte[] fileKey);

    /// <summary>
    /// Decrypts file data
    /// </summary>
    byte[] DecryptFileData(byte[] encryptedData, byte[] fileKey);

    /// <summary>
    /// Encrypts FileInfo metadata
    /// </summary>
    SyncFileInfo EncryptFileInfo(SyncFileInfo fileInfo, byte[] folderKey);

    /// <summary>
    /// Decrypts FileInfo metadata
    /// </summary>
    SyncFileInfo DecryptFileInfo(SyncFileInfo encryptedFileInfo, byte[] folderKey);

    /// <summary>
    /// Encrypts filename for storage
    /// </summary>
    string EncryptFilename(string filename, byte[] folderKey);

    /// <summary>
    /// Decrypts filename from storage
    /// </summary>
    string DecryptFilename(string encryptedFilename, byte[] folderKey);

    /// <summary>
    /// Generates password token for device authentication
    /// </summary>
    byte[] GeneratePasswordToken(string folderId, string password);

    /// <summary>
    /// Verifies password token
    /// </summary>
    bool VerifyPasswordToken(string folderId, string password, byte[] token);

    /// <summary>
    /// Checks if folder is encrypted
    /// </summary>
    bool IsFolderEncrypted(string folderId);

    /// <summary>
    /// Sets folder encryption password
    /// </summary>
    void SetFolderPassword(string folderId, string password);

    /// <summary>
    /// Removes folder encryption
    /// </summary>
    void RemoveFolderEncryption(string folderId);

    /// <summary>
    /// Gets encryption info for folder
    /// </summary>
    EncryptionInfo? GetFolderEncryptionInfo(string folderId);
}

/// <summary>
/// Encryption information for a folder
/// </summary>
public class EncryptionInfo
{
    public string FolderId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public byte[]? PasswordToken { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}