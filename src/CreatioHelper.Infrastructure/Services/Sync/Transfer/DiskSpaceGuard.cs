using CreatioHelper.Application.DTOs;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

public sealed record DiskSpaceCheck(bool Allowed, long FreeBytes, long RequiredFreeBytes, string Reason);

/// <summary>
/// Enforces the folder's minDiskFree setting before data is written to it.
/// Mirrors Syncthing's behaviour of refusing to pull when free space would drop
/// below the configured floor.
/// </summary>
public static class DiskSpaceGuard
{
    public static DiskSpaceCheck Check(string targetPath, string? minDiskFree, long incomingBytes)
    {
        if (string.IsNullOrWhiteSpace(minDiskFree))
        {
            return new DiskSpaceCheck(true, -1, 0, "minDiskFree is not configured");
        }

        DriveInfo drive;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrEmpty(root))
            {
                return new DiskSpaceCheck(true, -1, 0, "drive root could not be determined");
            }
            drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return new DiskSpaceCheck(true, -1, 0, "drive is not ready");
            }
        }
        catch (Exception ex)
        {
            return new DiskSpaceCheck(true, -1, 0, $"free space could not be determined: {ex.Message}");
        }

        var required = RequiredFreeBytes(minDiskFree, drive.TotalSize);
        if (required <= 0)
        {
            return new DiskSpaceCheck(true, drive.AvailableFreeSpace, 0, "threshold resolves to zero");
        }

        var freeAfter = drive.AvailableFreeSpace - Math.Max(0, incomingBytes);
        if (freeAfter >= required)
        {
            return new DiskSpaceCheck(true, drive.AvailableFreeSpace, required, "enough free space");
        }

        return new DiskSpaceCheck(
            false,
            drive.AvailableFreeSpace,
            required,
            $"writing {incomingBytes} byte(s) would leave {freeAfter} free, below the configured minimum of {required}");
    }

    private static long RequiredFreeBytes(string minDiskFree, long totalSize)
    {
        var parsed = FolderMinDiskFree.Parse(minDiskFree);
        if (parsed.Value <= 0)
        {
            return 0;
        }

        return parsed.Unit switch
        {
            "%" => (long)(totalSize * parsed.Value / 100.0),
            "kB" => (long)(parsed.Value * 1024),
            "MB" => (long)(parsed.Value * 1024 * 1024),
            "GB" => (long)(parsed.Value * 1024 * 1024 * 1024),
            "TB" => (long)(parsed.Value * 1024L * 1024 * 1024 * 1024),
            _ => 0
        };
    }
}
