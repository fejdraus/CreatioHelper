using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CreatioHelper.Infrastructure.Services.FileSystem;

/// <summary>
/// Windows implementation for handling junction points, symlinks, and reparse points.
/// </summary>
public class JunctionPointHandler : IJunctionPointHandler
{
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

    /// <summary>
    /// Checks if the specified path is a junction point.
    /// </summary>
    public bool IsJunctionPoint(string path)
    {
        if (!Directory.Exists(path))
            return false;

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return false;

        return GetReparseTag(path) == IO_REPARSE_TAG_MOUNT_POINT;
    }

    /// <summary>
    /// Gets the target path of a junction point.
    /// </summary>
    public string? GetJunctionTarget(string path)
    {
        if (!IsJunctionPoint(path) && !IsSymlink(path))
            return null;

        try
        {
            var targetInfo = new DirectoryInfo(path);
            if (targetInfo.LinkTarget is not null)
                return Path.GetFullPath(targetInfo.LinkTarget, Path.GetDirectoryName(path) ?? path);

            // For files, check FileInfo
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.LinkTarget is not null)
                    return Path.GetFullPath(fileInfo.LinkTarget, Path.GetDirectoryName(path) ?? path);
            }
        }
        catch
        {
            // Fall through to return null
        }

        return null;
    }

    /// <summary>
    /// Checks if the specified path is a symbolic link.
    /// </summary>
    public bool IsSymlink(string path)
    {
        if (!PathExists(path))
            return false;

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return false;

        return GetReparseTag(path) == IO_REPARSE_TAG_SYMLINK;
    }

    /// <summary>
    /// Checks if the specified path is any type of reparse point (junction, symlink, etc.).
    /// </summary>
    public bool IsReparsePoint(string path)
    {
        if (!PathExists(path))
            return false;

        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool PathExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    private static uint GetReparseTag(string path)
    {
        try
        {
            // Use .NET 6+ APIs to get reparse point info
            if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                if (dirInfo.LinkTarget != null)
                {
                    // It's a symlink if LinkTarget is set
                    return IO_REPARSE_TAG_SYMLINK;
                }
                // Check if it's a junction point (mount point)
                var attributes = dirInfo.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // On Windows, we need to distinguish between junction and symlink
                    // Junction points are typically created with mklink /J
                    // We can use the UnixFileMode or check via other means
                    return TryGetReparseTagFromPath(path);
                }
            }
            else if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.LinkTarget != null)
                {
                    return IO_REPARSE_TAG_SYMLINK;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return 0;
    }

    private static uint TryGetReparseTagFromPath(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return 0;

        try
        {
            // Check if LinkTarget is null - junctions don't report LinkTarget the same way
            var dirInfo = new DirectoryInfo(path);

            // If it has ReparsePoint attribute but no LinkTarget, it's likely a junction
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (dirInfo.LinkTarget == null)
                {
                    // This is typically a junction point on Windows
                    return IO_REPARSE_TAG_MOUNT_POINT;
                }
                return IO_REPARSE_TAG_SYMLINK;
            }
        }
        catch
        {
            // Ignore
        }

        return 0;
    }
}
