using System;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Compares file modification times with configurable tolerance window.
/// This is especially useful for FAT filesystems (2-second resolution)
/// and network filesystems where time synchronization may vary.
/// Based on Syncthing's ModTimeWindowS configuration.
/// </summary>
public interface IModificationTimeComparer
{
    /// <summary>
    /// Compare two modification times within the configured tolerance window.
    /// </summary>
    /// <param name="time1">First timestamp to compare</param>
    /// <param name="time2">Second timestamp to compare</param>
    /// <param name="windowSeconds">Tolerance window in seconds (0 = exact match)</param>
    /// <returns>True if times are considered equal within the window</returns>
    bool AreTimesEqual(DateTime time1, DateTime time2, int windowSeconds);

    /// <summary>
    /// Check if time1 is newer than time2, considering the tolerance window.
    /// </summary>
    bool IsNewer(DateTime time1, DateTime time2, int windowSeconds);

    /// <summary>
    /// Check if time1 is older than time2, considering the tolerance window.
    /// </summary>
    bool IsOlder(DateTime time1, DateTime time2, int windowSeconds);

    /// <summary>
    /// Get the recommended window size for a given filesystem type.
    /// </summary>
    int GetRecommendedWindowForFilesystem(string filesystemType);
}

/// <summary>
/// Implementation of modification time comparison with tolerance window.
/// </summary>
public class ModificationTimeComparer : IModificationTimeComparer
{
    // Known filesystem time resolutions
    private static readonly Dictionary<string, int> FilesystemWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fat"] = 2,      // FAT has 2-second resolution
        ["fat32"] = 2,
        ["vfat"] = 2,
        ["exfat"] = 1,    // exFAT has 10ms resolution, but use 1s for safety
        ["ntfs"] = 0,     // NTFS has 100ns resolution
        ["ext4"] = 0,     // ext4 has nanosecond resolution
        ["ext3"] = 0,
        ["apfs"] = 0,     // APFS has nanosecond resolution
        ["hfs+"] = 1,     // HFS+ has 1-second resolution
        ["nfs"] = 1,      // NFS can have clock skew
        ["smb"] = 2,      // SMB/CIFS may have resolution issues
        ["cifs"] = 2,
        ["sshfs"] = 1,    // SSHFS may have clock differences
        ["basic"] = 0,    // Default - no window
    };

    /// <inheritdoc />
    public bool AreTimesEqual(DateTime time1, DateTime time2, int windowSeconds)
    {
        if (windowSeconds <= 0)
        {
            return time1 == time2;
        }

        var diff = Math.Abs((time1 - time2).TotalSeconds);
        return diff <= windowSeconds;
    }

    /// <inheritdoc />
    public bool IsNewer(DateTime time1, DateTime time2, int windowSeconds)
    {
        if (windowSeconds <= 0)
        {
            return time1 > time2;
        }

        var diff = (time1 - time2).TotalSeconds;
        return diff > windowSeconds;
    }

    /// <inheritdoc />
    public bool IsOlder(DateTime time1, DateTime time2, int windowSeconds)
    {
        if (windowSeconds <= 0)
        {
            return time1 < time2;
        }

        var diff = (time2 - time1).TotalSeconds;
        return diff > windowSeconds;
    }

    /// <inheritdoc />
    public int GetRecommendedWindowForFilesystem(string filesystemType)
    {
        if (string.IsNullOrEmpty(filesystemType))
        {
            return 0;
        }

        return FilesystemWindows.TryGetValue(filesystemType, out var window) ? window : 0;
    }
}

/// <summary>
/// Результат сравнения времени модификации
/// </summary>
public enum ModificationTimeComparison
{
    /// <summary>Времена равны в пределах допуска</summary>
    Equal,

    /// <summary>Первое время новее второго</summary>
    Newer,

    /// <summary>Первое время старше второго</summary>
    Older
}

/// <summary>
/// Extension methods for modification time comparison.
/// </summary>
public static class ModificationTimeComparerExtensions
{
    /// <summary>
    /// Compare two times and return the comparison result.
    /// </summary>
    public static ModificationTimeComparison Compare(
        this IModificationTimeComparer comparer,
        DateTime time1,
        DateTime time2,
        int windowSeconds)
    {
        if (comparer.AreTimesEqual(time1, time2, windowSeconds))
        {
            return ModificationTimeComparison.Equal;
        }

        return comparer.IsNewer(time1, time2, windowSeconds)
            ? ModificationTimeComparison.Newer
            : ModificationTimeComparison.Older;
    }

    /// <summary>
    /// Check if a file needs to be updated based on modification times.
    /// Returns true if localTime is older than remoteTime beyond the window.
    /// </summary>
    public static bool NeedsUpdate(
        this IModificationTimeComparer comparer,
        DateTime localTime,
        DateTime remoteTime,
        int windowSeconds)
    {
        return comparer.IsOlder(localTime, remoteTime, windowSeconds);
    }
}
