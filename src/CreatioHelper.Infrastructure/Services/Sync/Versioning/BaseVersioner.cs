using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Security.AccessControl;
using System.Runtime.InteropServices;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// Base class for all versioning implementations
/// Provides common functionality for file archiving, version management, and cleanup
/// Compatible with Syncthing's versioning utilities
/// </summary>
public abstract class BaseVersioner : IVersioner
{
    protected readonly ILogger _logger;
    protected readonly string _folderPath;
    protected readonly VersioningConfiguration _config;
    
    // Syncthing-compatible timestamp format: "20060102-150405"
    protected const string TimeFormat = "yyyyMMdd-HHmmss";
    private const string VersionFilePattern = @"^(.+)~(\d{8}-\d{6})(\..+)?$";
    private static readonly Regex VersionFileRegex = new(VersionFilePattern, RegexOptions.Compiled);
    
    // Mutex for atomic file operations to prevent race conditions
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    
    // Windows temp prefix like Syncthing
    private const string WindowsTempPrefix = "~syncthing~";
    private const string UnixTempPrefix = ".syncthing.";

    protected BaseVersioner(ILogger logger, string folderPath, VersioningConfiguration config)
    {
        _logger = logger;
        _folderPath = folderPath;
        _config = config;
        
        // Ensure versions directory exists
        Directory.CreateDirectory(VersionsPath);
    }

    public abstract string VersionerType { get; }

    public virtual string VersionsPath => _config.FSPath.StartsWith("/") || _config.FSPath.Contains(":\\") 
        ? _config.FSPath 
        : Path.Combine(_folderPath, _config.FSPath);

    public virtual async Task ArchiveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_folderPath, filePath);
        
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Cannot archive non-existent file: {FilePath}", filePath);
            return;
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            var versionTime = DateTime.UtcNow;
            var versionPath = GetVersionPath(filePath, versionTime);
            
            // Create directory structure in versions folder
            var versionDir = Path.GetDirectoryName(versionPath);
            if (!string.IsNullOrEmpty(versionDir) && !Directory.Exists(versionDir))
            {
                Directory.CreateDirectory(versionDir);
                
                // Copy directory permissions from source
                var sourceDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(sourceDir) && Directory.Exists(sourceDir))
                {
                    CopyDirectoryPermissions(sourceDir, versionDir);
                }
            }

            // Copy file to versions directory
            await CopyFileWithMetadataAsync(fullPath, versionPath, cancellationToken).ConfigureAwait(false);
            
            _logger.LogInformation("Archived file {FilePath} as version {VersionTime:yyyy-MM-dd HH:mm:ss}", 
                filePath, versionTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive file {FilePath}: {Error}", filePath, ex.Message);
            throw;
        }
    }

    public virtual async Task<Dictionary<string, List<FileVersion>>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = new Dictionary<string, List<FileVersion>>();

        try
        {
            if (!Directory.Exists(VersionsPath))
                return versions;

            await foreach (var versionFile in EnumerateVersionFilesAsync(VersionsPath, cancellationToken))
            {
                var (originalPath, versionTime) = ParseVersionFileName(versionFile);
                if (originalPath == null || versionTime == null)
                    continue;

                if (!versions.ContainsKey(originalPath))
                    versions[originalPath] = new List<FileVersion>();

                var fileInfo = new FileInfo(versionFile);
                versions[originalPath].Add(new FileVersion
                {
                    VersionTime = versionTime.Value,
                    ModTime = fileInfo.LastWriteTime,
                    Size = fileInfo.Length,
                    VersionPath = versionFile,
                    OriginalPath = originalPath
                });
            }

            // Sort versions by time (newest first)
            foreach (var versionList in versions.Values)
            {
                versionList.Sort((a, b) => b.VersionTime.CompareTo(a.VersionTime));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get versions: {Error}", ex.Message);
        }

        return versions;
    }

    public virtual async Task RestoreAsync(string filePath, DateTime versionTime, CancellationToken cancellationToken = default)
    {
        var versionPath = GetVersionPath(filePath, versionTime);
        
        if (!File.Exists(versionPath))
        {
            throw new FileNotFoundException($"Version not found: {filePath} @ {versionTime:yyyy-MM-dd HH:mm:ss}");
        }

        var targetPath = Path.Combine(_folderPath, filePath);
        var targetDir = Path.GetDirectoryName(targetPath);
        
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // Archive current file if it exists
        if (File.Exists(targetPath))
        {
            await ArchiveAsync(filePath, cancellationToken).ConfigureAwait(false);
        }

        // Restore the version
        await CopyFileWithMetadataAsync(versionPath, targetPath, cancellationToken).ConfigureAwait(false);
        
        _logger.LogInformation("Restored file {FilePath} from version {VersionTime:yyyy-MM-dd HH:mm:ss}", 
            filePath, versionTime);
    }

    public abstract Task CleanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the versioned file path for a given original file and version time
    /// Format: filename~20060102-150405.ext
    /// </summary>
    protected virtual string GetVersionPath(string originalPath, DateTime versionTime)
    {
        // Security: Sanitize path to prevent directory traversal attacks
        var sanitizedPath = SanitizePath(originalPath);
        
        var fileName = Path.GetFileName(sanitizedPath);
        var directory = Path.GetDirectoryName(sanitizedPath) ?? "";
        var extension = Path.GetExtension(fileName);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        
        var versionFileName = $"{nameWithoutExt}~{versionTime.ToString(TimeFormat)}{extension}";
        var versionDir = Path.Combine(VersionsPath, directory);
        
        return Path.Combine(versionDir, versionFileName);
    }

    /// <summary>
    /// Parses a version file name to extract original path and version time
    /// </summary>
    protected virtual (string? originalPath, DateTime? versionTime) ParseVersionFileName(string versionFilePath)
    {
        var relativePath = Path.GetRelativePath(VersionsPath, versionFilePath);
        var fileName = Path.GetFileName(relativePath);
        var directory = Path.GetDirectoryName(relativePath) ?? "";
        
        var match = VersionFileRegex.Match(fileName);
        if (!match.Success)
            return (null, null);

        var originalName = match.Groups[1].Value;
        var timestampStr = match.Groups[2].Value;
        var extension = match.Groups[3].Value;

        if (!DateTime.TryParseExact(timestampStr, TimeFormat, null, 
            System.Globalization.DateTimeStyles.None, out var versionTime))
            return (null, null);

        var originalFileName = originalName + extension;
        var originalPath = string.IsNullOrEmpty(directory) 
            ? originalFileName 
            : Path.Combine(directory, originalFileName);

        return (originalPath, versionTime);
    }

    /// <summary>
    /// Enumerates all version files recursively
    /// </summary>
    protected virtual async IAsyncEnumerable<string> EnumerateVersionFilesAsync(string directory, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var file in EnumerateFilesRecursiveAsync(directory, cancellationToken))
        {
            if (IsVersionFile(file))
                yield return file;
        }
    }

    /// <summary>
    /// Checks if a file is a version file based on naming pattern
    /// </summary>
    protected virtual bool IsVersionFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return VersionFileRegex.IsMatch(fileName);
    }

    /// <summary>
    /// Copies a file while preserving metadata (modification time, permissions)
    /// Uses Syncthing-inspired approach with proper file locking and Windows-specific handling
    /// </summary>
    protected virtual async Task CopyFileWithMetadataAsync(string sourcePath, string destPath, CancellationToken cancellationToken = default)
    {
        // Use semaphore to prevent race conditions during file operations
        await FileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            await RenameOrCopyWithRetryAsync(sourcePath, destPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }
    
    /// <summary>
    /// Syncthing-inspired RenameOrCopy with Windows file locking handling
    /// </summary>
    private async Task RenameOrCopyWithRetryAsync(string sourcePath, string destPath, CancellationToken cancellationToken)
    {
        // Generate unique temporary file name like Syncthing
        var tempPath = GenerateTempFileName(destPath);
        var sourceInfo = new FileInfo(sourcePath);
        
        if (!sourceInfo.Exists)
        {
            throw new FileNotFoundException($"Source file not found: {sourcePath}");
        }

        try
        {
            // Ensure destination directory is writable (Windows-specific)
            await EnsureDestinationWritableAsync(destPath, cancellationToken).ConfigureAwait(false);
            
            // Copy file content atomically
            await CopyFileAtomicallyAsync(sourcePath, tempPath, sourceInfo, cancellationToken).ConfigureAwait(false);
            
            // Atomic rename with Windows-specific error handling
            await AtomicRenameAsync(tempPath, destPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Clean up temp file if operation failed
            await CleanupTempFileAsync(tempPath).ConfigureAwait(false);
            throw;
        }
    }
    
    /// <summary>
    /// Generates a unique temporary file name using Syncthing approach
    /// </summary>
    private static string GenerateTempFileName(string originalPath)
    {
        var dir = Path.GetDirectoryName(originalPath) ?? "";
        var fileName = Path.GetFileName(originalPath);
        var tempPrefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsTempPrefix : UnixTempPrefix;
        
        // Add timestamp to make it unique
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var tempFileName = $"{tempPrefix}{fileName}.{timestamp}.tmp";
        
        return Path.Combine(dir, tempFileName);
    }
    
    /// <summary>
    /// Ensures destination directory and file are writable (Windows-specific)
    /// </summary>
    private Task EnsureDestinationWritableAsync(string destPath, CancellationToken cancellationToken)
    {
        try
        {
            var destDir = Path.GetDirectoryName(destPath);
            if (string.IsNullOrEmpty(destDir))
                return Task.CompletedTask;

            // Create directory if it doesn't exist
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // On Windows, ensure destination file is writable if it exists
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(destPath))
            {
                try
                {
                    // Remove read-only attribute if present
                    var attributes = File.GetAttributes(destPath);
                    if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        File.SetAttributes(destPath, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove read-only attribute from {FilePath}", destPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure destination writable: {DestPath}", destPath);
        }
        
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Copies file content atomically with proper stream handling
    /// </summary>
    private async Task CopyFileAtomicallyAsync(string sourcePath, string tempPath, FileInfo sourceInfo, CancellationToken cancellationToken)
    {
        const int bufferSize = 81920; // 80KB buffer for better performance
        
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        
        await source.CopyToAsync(dest, bufferSize, cancellationToken).ConfigureAwait(false);
        
        // Ensure data is written to disk
        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
        
        // Preserve file metadata on temp file
        PreserveFileMetadata(sourceInfo, tempPath);
    }
    
    /// <summary>
    /// Preserves file metadata (timestamps and attributes)
    /// </summary>
    private void PreserveFileMetadata(FileInfo sourceInfo, string destPath)
    {
        try
        {
            File.SetCreationTime(destPath, sourceInfo.CreationTime);
            File.SetLastWriteTime(destPath, sourceInfo.LastWriteTime);
            File.SetLastAccessTime(destPath, sourceInfo.LastAccessTime);
            
            // Copy attributes (except read-only which we'll handle separately)
            var attributes = sourceInfo.Attributes & ~FileAttributes.ReadOnly;
            File.SetAttributes(destPath, attributes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to preserve file metadata for {FilePath}", destPath);
        }
    }
    
    /// <summary>
    /// Performs atomic rename with Windows-specific error handling and retry logic
    /// </summary>
    private async Task AtomicRenameAsync(string tempPath, string destPath, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var delay = TimeSpan.FromMilliseconds(100);
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // On Windows, remove destination file first if it exists and names are different
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(destPath) && 
                    !string.Equals(tempPath, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Remove read-only attribute and delete
                        var attributes = File.GetAttributes(destPath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(destPath, attributes & ~FileAttributes.ReadOnly);
                        }
                        File.Delete(destPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to prepare destination file for rename: {DestPath}", destPath);
                    }
                }
                
                // Attempt the rename
                File.Move(tempPath, destPath, overwrite: true);
                return; // Success!
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                // Windows-specific: check if it's a permission/locking error
                var isWindowsLockingError = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && 
                    (ex.Message.Contains("being used by another process") || 
                     ex.Message.Contains("access") || 
                     ex.HResult == -2147024864); // ERROR_SHARING_VIOLATION
                
                if (isWindowsLockingError)
                {
                    _logger.LogWarning("File locking detected on attempt {Attempt}/{MaxRetries}, retrying after delay: {Error}", 
                        attempt, maxRetries, ex.Message);
                    
                    await Task.Delay(delay * attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                
                throw;
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxRetries && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogWarning("Access denied on attempt {Attempt}/{MaxRetries}, trying to fix permissions: {Error}", 
                    attempt, maxRetries, ex.Message);
                
                // Try to fix permissions on destination
                try
                {
                    if (File.Exists(destPath))
                    {
                        var attributes = File.GetAttributes(destPath);
                        File.SetAttributes(destPath, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // Ignore permission fix errors
                }
                
                await Task.Delay(delay * attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }
        }
        
        // If we reach here, all retries failed
        throw new IOException($"Failed to rename {tempPath} to {destPath} after {maxRetries} attempts");
    }
    
    /// <summary>
    /// Safely cleans up temporary file
    /// </summary>
    private Task CleanupTempFileAsync(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                // On Windows, ensure file is not read-only before deletion
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var attributes = File.GetAttributes(tempPath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(tempPath, attributes & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch
                    {
                        // Ignore attribute errors
                    }
                }
                
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup temporary file: {TempPath}", tempPath);
            // Don't throw - cleanup errors shouldn't fail the main operation
        }
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies directory permissions from source to destination
    /// </summary>
    protected virtual void CopyDirectoryPermissions(string sourceDir, string destDir)
    {
        try
        {
            var sourceInfo = new DirectoryInfo(sourceDir);
            var destInfo = new DirectoryInfo(destDir);
            
            // Copy timestamps
            destInfo.CreationTime = sourceInfo.CreationTime;
            destInfo.LastWriteTime = sourceInfo.LastWriteTime;
            destInfo.LastAccessTime = sourceInfo.LastAccessTime;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy directory permissions from {Source} to {Dest}", sourceDir, destDir);
        }
    }

    /// <summary>
    /// Enumerates files recursively in a directory
    /// </summary>
    protected virtual async IAsyncEnumerable<string> EnumerateFilesRecursiveAsync(string directory, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
            yield break;

        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var currentDir = stack.Pop();
            
            // Enumerate files
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                yield return file;
                await Task.Yield(); // Allow other async operations
            }
            
            // Enumerate subdirectories
            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                stack.Push(subDir);
            }
        }
    }

    /// <summary>
    /// Removes empty directories recursively
    /// </summary>
    protected virtual void RemoveEmptyDirectories(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            foreach (var subDir in Directory.GetDirectories(directory))
            {
                RemoveEmptyDirectories(subDir);
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                _logger.LogDebug("Removed empty directory: {Directory}", directory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove empty directory {Directory}: {Error}", directory, ex.Message);
        }
    }

    /// <summary>
    /// Dispose resources (virtual for derived classes)
    /// </summary>
    public virtual void Dispose()
    {
        // Base implementation does nothing
        // Derived classes can override if needed
    }
    
    /// <summary>
    /// Sanitizes a file path to prevent directory traversal attacks
    /// Removes any attempts to navigate outside the intended directory
    /// </summary>
    protected static string SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
            
        // Normalize path separators
        path = path.Replace('\\', '/');
        
        // Split into components and filter out dangerous ones
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safeParts = new List<string>();
        
        foreach (var part in parts)
        {
            // Skip dangerous path components
            if (part == "." || part == ".." || string.IsNullOrWhiteSpace(part))
                continue;
                
            // Skip parts with dangerous characters
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                continue;
                
            safeParts.Add(part);
        }
        
        // Rejoin with platform-appropriate separator
        return string.Join(Path.DirectorySeparatorChar.ToString(), safeParts);
    }
}