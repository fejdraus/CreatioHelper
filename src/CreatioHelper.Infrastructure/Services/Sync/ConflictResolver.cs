using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Conflict resolution service (based on Syncthing conflict handling)
/// Inspired by Syncthing's lib/model conflict resolution logic
/// </summary>
public class ConflictResolver
{
    private readonly ILogger<ConflictResolver> _logger;

    public ConflictResolver(ILogger<ConflictResolver> logger)
    {
        _logger = logger;
    }

    public async Task<ConflictResolution> ResolveConflictAsync(
        SyncFileInfo localFile, 
        SyncFileInfo remoteFile, 
        string deviceId,
        ConflictResolutionStrategy strategy = ConflictResolutionStrategy.Default)
    {
        try
        {
            _logger.LogDebug("Resolving conflict for file {FileName} between local and device {DeviceId}", 
                localFile.Name, deviceId);

            // Check if files are actually different
            if (localFile.Hash == remoteFile.Hash && localFile.Size == remoteFile.Size)
            {
                return new ConflictResolution
                {
                    Action = ConflictAction.NoAction,
                    Reason = "Files are identical"
                };
            }

            // Use vector clocks to determine which version is newer
            if (localFile.Vector.IsNewerThan(remoteFile.Vector))
            {
                return new ConflictResolution
                {
                    Action = ConflictAction.KeepLocal,
                    Reason = "Local version is newer based on vector clock"
                };
            }

            if (remoteFile.Vector.IsNewerThan(localFile.Vector))
            {
                return new ConflictResolution
                {
                    Action = ConflictAction.AcceptRemote,
                    Reason = "Remote version is newer based on vector clock"
                };
            }

            // Vector clocks are concurrent - we have a conflict
            return await ResolveRealConflictAsync(localFile, remoteFile, deviceId, strategy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving conflict for file {FileName}", localFile.Name);
            return new ConflictResolution
            {
                Action = ConflictAction.CreateConflictCopy,
                Reason = $"Error during resolution: {ex.Message}"
            };
        }
    }

    private Task<ConflictResolution> ResolveRealConflictAsync(
        SyncFileInfo localFile,
        SyncFileInfo remoteFile,
        string deviceId,
        ConflictResolutionStrategy strategy)
    {
        switch (strategy)
        {
            case ConflictResolutionStrategy.PreferLocal:
                return Task.FromResult(new ConflictResolution
                {
                    Action = ConflictAction.KeepLocal,
                    ConflictCopyName = GenerateConflictFileName(localFile.Name, deviceId),
                    Reason = "Strategy: Prefer local"
                });

            case ConflictResolutionStrategy.PreferRemote:
                return Task.FromResult(new ConflictResolution
                {
                    Action = ConflictAction.AcceptRemote,
                    ConflictCopyName = GenerateConflictFileName(localFile.Name, "local"),
                    Reason = "Strategy: Prefer remote"
                });

            case ConflictResolutionStrategy.PreferNewer:
                if (localFile.ModifiedTime > remoteFile.ModifiedTime)
                {
                    return Task.FromResult(new ConflictResolution
                    {
                        Action = ConflictAction.KeepLocal,
                        ConflictCopyName = GenerateConflictFileName(localFile.Name, deviceId),
                        Reason = "Local file is newer"
                    });
                }
                else
                {
                    return Task.FromResult(new ConflictResolution
                    {
                        Action = ConflictAction.AcceptRemote,
                        ConflictCopyName = GenerateConflictFileName(localFile.Name, "local"),
                        Reason = "Remote file is newer"
                    });
                }

            case ConflictResolutionStrategy.PreferLarger:
                if (localFile.Size > remoteFile.Size)
                {
                    return Task.FromResult(new ConflictResolution
                    {
                        Action = ConflictAction.KeepLocal,
                        ConflictCopyName = GenerateConflictFileName(localFile.Name, deviceId),
                        Reason = "Local file is larger"
                    });
                }
                else
                {
                    return Task.FromResult(new ConflictResolution
                    {
                        Action = ConflictAction.AcceptRemote,
                        ConflictCopyName = GenerateConflictFileName(localFile.Name, "local"),
                        Reason = "Remote file is larger"
                    });
                }

            case ConflictResolutionStrategy.CreateBothCopies:
                return Task.FromResult(new ConflictResolution
                {
                    Action = ConflictAction.CreateBothCopies,
                    ConflictCopyName = GenerateConflictFileName(localFile.Name, deviceId),
                    LocalCopyName = GenerateConflictFileName(localFile.Name, "local"),
                    Reason = "Strategy: Keep both versions"
                });

            case ConflictResolutionStrategy.Default:
            default:
                // Default Syncthing behavior: rename the local file and accept remote
                return Task.FromResult(new ConflictResolution
                {
                    Action = ConflictAction.CreateConflictCopy,
                    ConflictCopyName = GenerateConflictFileName(localFile.Name, "local"),
                    Reason = "Default conflict resolution: create conflict copy of local file"
                });
        }
    }

    public async Task<bool> ApplyResolutionAsync(ConflictResolution resolution, string folderPath, SyncFileInfo localFile, SyncFileInfo remoteFile)
    {
        try
        {
            var localFilePath = Path.Combine(folderPath, localFile.RelativePath);
            
            switch (resolution.Action)
            {
                case ConflictAction.NoAction:
                    return true;

                case ConflictAction.KeepLocal:
                    if (!string.IsNullOrEmpty(resolution.ConflictCopyName))
                    {
                        // Create conflict copy of remote version (conceptually)
                        _logger.LogInformation("Keeping local version of {FileName}, remote version would be saved as conflict copy", localFile.Name);
                    }
                    return true;

                case ConflictAction.AcceptRemote:
                    if (!string.IsNullOrEmpty(resolution.ConflictCopyName))
                    {
                        // Create conflict copy of local version
                        var conflictPath = Path.Combine(folderPath, Path.GetDirectoryName(localFile.RelativePath) ?? "", resolution.ConflictCopyName);
                        await CreateConflictCopyAsync(localFilePath, conflictPath);
                    }
                    // The remote file will be downloaded by the sync engine
                    return true;

                case ConflictAction.CreateConflictCopy:
                    if (!string.IsNullOrEmpty(resolution.ConflictCopyName))
                    {
                        var conflictPath = Path.Combine(folderPath, Path.GetDirectoryName(localFile.RelativePath) ?? "", resolution.ConflictCopyName);
                        await CreateConflictCopyAsync(localFilePath, conflictPath);
                    }
                    return true;

                case ConflictAction.CreateBothCopies:
                    if (!string.IsNullOrEmpty(resolution.ConflictCopyName))
                    {
                        var remoteConflictPath = Path.Combine(folderPath, Path.GetDirectoryName(localFile.RelativePath) ?? "", resolution.ConflictCopyName);
                        // Remote conflict copy will be created when remote file is downloaded
                    }
                    if (!string.IsNullOrEmpty(resolution.LocalCopyName))
                    {
                        var localConflictPath = Path.Combine(folderPath, Path.GetDirectoryName(localFile.RelativePath) ?? "", resolution.LocalCopyName);
                        await CreateConflictCopyAsync(localFilePath, localConflictPath);
                    }
                    return true;

                default:
                    _logger.LogWarning("Unknown conflict resolution action: {Action}", resolution.Action);
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying conflict resolution for file {FileName}", localFile.Name);
            return false;
        }
    }

    private Task CreateConflictCopyAsync(string sourcePath, string conflictPath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                _logger.LogWarning("Source file does not exist: {SourcePath}", sourcePath);
                return Task.CompletedTask;
            }

            var conflictDir = Path.GetDirectoryName(conflictPath);
            if (!string.IsNullOrEmpty(conflictDir) && !Directory.Exists(conflictDir))
            {
                Directory.CreateDirectory(conflictDir);
            }

            File.Copy(sourcePath, conflictPath, overwrite: true);
            _logger.LogInformation("Created conflict copy: {ConflictPath}", conflictPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating conflict copy from {SourcePath} to {ConflictPath}", sourcePath, conflictPath);
            throw;
        }
        
        return Task.CompletedTask;
    }

    private string GenerateConflictFileName(string originalName, string deviceId)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(originalName);
        var extension = Path.GetExtension(originalName);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        
        // Syncthing-style conflict file naming
        return $"{nameWithoutExt}.sync-conflict-{timestamp}-{deviceId.Substring(0, Math.Min(7, deviceId.Length))}{extension}";
    }
}

public class ConflictResolution
{
    public ConflictAction Action { get; set; } = ConflictAction.NoAction;
    public string Reason { get; set; } = string.Empty;
    public string? ConflictCopyName { get; set; }
    public string? LocalCopyName { get; set; }
}

public enum ConflictAction
{
    NoAction,
    KeepLocal,
    AcceptRemote,
    CreateConflictCopy,
    CreateBothCopies
}

public enum ConflictResolutionStrategy
{
    Default,        // Create conflict copy and accept remote (Syncthing default)
    PreferLocal,    // Always prefer local version
    PreferRemote,   // Always prefer remote version
    PreferNewer,    // Prefer version with newer timestamp
    PreferLarger,   // Prefer version with larger file size
    CreateBothCopies // Keep both versions with different names
}