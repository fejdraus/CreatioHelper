using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Service for handling the IgnorePermissions folder option.
/// Based on Syncthing's ignorePerms folder configuration.
/// When enabled, file permissions are not synchronized.
/// </summary>
public interface IIgnorePermissionsService
{
    /// <summary>
    /// Check if permissions should be ignored for a folder.
    /// </summary>
    bool ShouldIgnorePermissions(string folderId);

    /// <summary>
    /// Set ignore permissions for a folder.
    /// </summary>
    void SetIgnorePermissions(string folderId, bool ignore);

    /// <summary>
    /// Check if two file modes are equal (considering ignore settings).
    /// </summary>
    bool ArePermissionsEqual(string folderId, FilePermissions local, FilePermissions remote);

    /// <summary>
    /// Get the permissions to apply for a file.
    /// </summary>
    FilePermissions GetEffectivePermissions(string folderId, FilePermissions requested, FilePermissions? existing = null);

    /// <summary>
    /// Get default permissions for new files.
    /// </summary>
    FilePermissions GetDefaultFilePermissions(string folderId);

    /// <summary>
    /// Get default permissions for new directories.
    /// </summary>
    FilePermissions GetDefaultDirectoryPermissions(string folderId);

    /// <summary>
    /// Get statistics about permission handling.
    /// </summary>
    PermissionStats GetStats(string folderId);
}

/// <summary>
/// Represents Unix-style file permissions.
/// </summary>
public struct FilePermissions : IEquatable<FilePermissions>
{
    /// <summary>
    /// Raw permission bits (Unix mode).
    /// </summary>
    public int Mode { get; init; }

    /// <summary>
    /// Owner read permission.
    /// </summary>
    public bool OwnerRead => (Mode & 0x100) != 0;

    /// <summary>
    /// Owner write permission.
    /// </summary>
    public bool OwnerWrite => (Mode & 0x080) != 0;

    /// <summary>
    /// Owner execute permission.
    /// </summary>
    public bool OwnerExecute => (Mode & 0x040) != 0;

    /// <summary>
    /// Group read permission.
    /// </summary>
    public bool GroupRead => (Mode & 0x020) != 0;

    /// <summary>
    /// Group write permission.
    /// </summary>
    public bool GroupWrite => (Mode & 0x010) != 0;

    /// <summary>
    /// Group execute permission.
    /// </summary>
    public bool GroupExecute => (Mode & 0x008) != 0;

    /// <summary>
    /// Other read permission.
    /// </summary>
    public bool OtherRead => (Mode & 0x004) != 0;

    /// <summary>
    /// Other write permission.
    /// </summary>
    public bool OtherWrite => (Mode & 0x002) != 0;

    /// <summary>
    /// Other execute permission.
    /// </summary>
    public bool OtherExecute => (Mode & 0x001) != 0;

    /// <summary>
    /// Whether the file is executable by owner.
    /// </summary>
    public bool IsExecutable => OwnerExecute;

    /// <summary>
    /// Standard file permissions (0644 = rw-r--r--).
    /// </summary>
    public static FilePermissions DefaultFile => new() { Mode = 0x1A4 }; // 0644

    /// <summary>
    /// Standard directory permissions (0755 = rwxr-xr-x).
    /// </summary>
    public static FilePermissions DefaultDirectory => new() { Mode = 0x1ED }; // 0755

    /// <summary>
    /// Executable file permissions (0755 = rwxr-xr-x).
    /// </summary>
    public static FilePermissions ExecutableFile => new() { Mode = 0x1ED }; // 0755

    /// <summary>
    /// No permissions.
    /// </summary>
    public static FilePermissions None => new() { Mode = 0 };

    public bool Equals(FilePermissions other) => Mode == other.Mode;
    public override bool Equals(object? obj) => obj is FilePermissions other && Equals(other);
    public override int GetHashCode() => Mode.GetHashCode();
    public static bool operator ==(FilePermissions left, FilePermissions right) => left.Equals(right);
    public static bool operator !=(FilePermissions left, FilePermissions right) => !left.Equals(right);

    public override string ToString() => $"{Convert.ToString(Mode, 8).PadLeft(4, '0')}";

    /// <summary>
    /// Create permissions from octal string (e.g., "0644").
    /// </summary>
    public static FilePermissions FromOctal(string octal)
    {
        return new FilePermissions { Mode = Convert.ToInt32(octal, 8) };
    }

    /// <summary>
    /// Create permissions from integer mode.
    /// </summary>
    public static FilePermissions FromMode(int mode)
    {
        return new FilePermissions { Mode = mode & 0x1FF }; // Mask to 9 permission bits
    }
}

/// <summary>
/// Statistics for permission handling.
/// </summary>
public class PermissionStats
{
    public string FolderId { get; init; } = string.Empty;
    public bool IgnorePermissions { get; set; }
    public long FilesWithPermissionsApplied { get; set; }
    public long FilesWithPermissionsIgnored { get; set; }
    public long PermissionMismatches { get; set; }
}

/// <summary>
/// Configuration for ignore permissions.
/// </summary>
public class IgnorePermissionsConfiguration
{
    /// <summary>
    /// Default setting for ignoring permissions.
    /// </summary>
    public bool DefaultIgnorePermissions { get; set; } = false;

    /// <summary>
    /// Per-folder override settings.
    /// </summary>
    public Dictionary<string, bool> FolderSettings { get; } = new();

    /// <summary>
    /// Default file permissions when ignoring (octal 0644).
    /// </summary>
    public int DefaultFileMode { get; set; } = 0x1A4; // 0644

    /// <summary>
    /// Default directory permissions when ignoring (octal 0755).
    /// </summary>
    public int DefaultDirectoryMode { get; set; } = 0x1ED; // 0755

    /// <summary>
    /// Whether to preserve execute bit even when ignoring permissions.
    /// </summary>
    public bool PreserveExecuteBit { get; set; } = true;

    /// <summary>
    /// Get effective setting for a folder.
    /// </summary>
    public bool GetEffectiveSetting(string folderId)
    {
        if (FolderSettings.TryGetValue(folderId, out var setting))
        {
            return setting;
        }
        return DefaultIgnorePermissions;
    }
}

/// <summary>
/// Implementation of ignore permissions service.
/// </summary>
public class IgnorePermissionsService : IIgnorePermissionsService
{
    private readonly ILogger<IgnorePermissionsService> _logger;
    private readonly IgnorePermissionsConfiguration _config;
    private readonly ConcurrentDictionary<string, PermissionStats> _stats = new();

    public IgnorePermissionsService(
        ILogger<IgnorePermissionsService> logger,
        IgnorePermissionsConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new IgnorePermissionsConfiguration();
    }

    /// <inheritdoc />
    public bool ShouldIgnorePermissions(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        return _config.GetEffectiveSetting(folderId);
    }

    /// <inheritdoc />
    public void SetIgnorePermissions(string folderId, bool ignore)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        _config.FolderSettings[folderId] = ignore;
        _logger.LogInformation("Set ignore permissions for folder {FolderId} to {Ignore}", folderId, ignore);

        // Update stats
        var stats = GetOrCreateStats(folderId);
        stats.IgnorePermissions = ignore;
    }

    /// <inheritdoc />
    public bool ArePermissionsEqual(string folderId, FilePermissions local, FilePermissions remote)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (ShouldIgnorePermissions(folderId))
        {
            // When ignoring permissions, only compare execute bit if configured
            if (_config.PreserveExecuteBit)
            {
                return local.IsExecutable == remote.IsExecutable;
            }
            return true; // All permissions considered equal
        }

        var stats = GetOrCreateStats(folderId);
        if (local != remote)
        {
            stats.PermissionMismatches++;
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    public FilePermissions GetEffectivePermissions(string folderId, FilePermissions requested, FilePermissions? existing = null)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var stats = GetOrCreateStats(folderId);

        if (ShouldIgnorePermissions(folderId))
        {
            stats.FilesWithPermissionsIgnored++;

            // Use existing permissions if available, otherwise use defaults
            if (existing.HasValue)
            {
                if (_config.PreserveExecuteBit && requested.IsExecutable != existing.Value.IsExecutable)
                {
                    // Update execute bit only
                    var mode = existing.Value.Mode;
                    if (requested.IsExecutable)
                    {
                        mode |= 0x049; // Add execute for owner, group, other
                    }
                    else
                    {
                        mode &= ~0x049; // Remove execute
                    }
                    return FilePermissions.FromMode(mode);
                }
                return existing.Value;
            }

            // Return default permissions (with execute if requested)
            var defaultMode = _config.DefaultFileMode;
            if (_config.PreserveExecuteBit && requested.IsExecutable)
            {
                defaultMode |= 0x049; // Add execute bits
            }
            return FilePermissions.FromMode(defaultMode);
        }

        stats.FilesWithPermissionsApplied++;
        return requested;
    }

    /// <inheritdoc />
    public FilePermissions GetDefaultFilePermissions(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        return FilePermissions.FromMode(_config.DefaultFileMode);
    }

    /// <inheritdoc />
    public FilePermissions GetDefaultDirectoryPermissions(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        return FilePermissions.FromMode(_config.DefaultDirectoryMode);
    }

    /// <inheritdoc />
    public PermissionStats GetStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        return GetOrCreateStats(folderId);
    }

    private PermissionStats GetOrCreateStats(string folderId)
    {
        return _stats.GetOrAdd(folderId, id => new PermissionStats
        {
            FolderId = id,
            IgnorePermissions = _config.GetEffectiveSetting(id)
        });
    }
}
