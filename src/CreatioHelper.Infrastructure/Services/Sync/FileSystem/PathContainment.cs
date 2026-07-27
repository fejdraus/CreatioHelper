namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Decides whether a path resolves inside a directory.
///
/// Written once because the obvious spelling is wrong: comparing the resolved
/// path against the directory with StartsWith accepts any sibling whose name
/// merely begins with the directory name, so a guard on "C:\data\sync" lets
/// "C:\data\syncbackup\secrets.txt" through. The separator has to be part of
/// the comparison.
/// </summary>
public static class PathContainment
{
    public static bool IsInside(string basePath, string candidatePath)
    {
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(candidatePath))
        {
            return false;
        }

        string baseFull;
        string candidateFull;

        try
        {
            baseFull = Path.GetFullPath(basePath);
            candidateFull = Path.GetFullPath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(baseFull, candidateFull, comparison))
        {
            return true;
        }

        var prefix = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        return candidateFull.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Resolves a relative path against a directory and returns it only when the
    /// result stays inside. Null means the caller must refuse the request.
    /// </summary>
    public static string? Resolve(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        string candidate;

        try
        {
            candidate = Path.GetFullPath(Path.Combine(basePath, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return IsInside(basePath, candidate) ? candidate : null;
    }
}
