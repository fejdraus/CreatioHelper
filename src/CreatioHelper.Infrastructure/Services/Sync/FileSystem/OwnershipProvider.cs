using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// File ownership information (based on Syncthing basicfs_unix.go)
/// </summary>
public record FileOwnership
{
    /// <summary>
    /// Unix user ID (UID) or Windows SID
    /// </summary>
    public string OwnerId { get; init; } = string.Empty;

    /// <summary>
    /// Unix group ID (GID) or Windows group SID
    /// </summary>
    public string GroupId { get; init; } = string.Empty;

    /// <summary>
    /// Owner name (resolved from ID)
    /// </summary>
    public string? OwnerName { get; init; }

    /// <summary>
    /// Group name (resolved from ID)
    /// </summary>
    public string? GroupName { get; init; }

    /// <summary>
    /// Unix file permissions (mode bits)
    /// </summary>
    public int UnixMode { get; init; }
}

/// <summary>
/// Provides file ownership operations (based on Syncthing basicfs_unix.go)
/// </summary>
public interface IOwnershipProvider
{
    /// <summary>
    /// Check if ownership operations are supported on this platform
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Get the ownership of a file or directory
    /// </summary>
    Task<FileOwnership?> GetOwnershipAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Set the ownership of a file or directory
    /// </summary>
    Task<bool> SetOwnershipAsync(string path, FileOwnership ownership, CancellationToken ct = default);

    /// <summary>
    /// Copy ownership from source to destination
    /// </summary>
    Task<bool> CopyOwnershipAsync(string sourcePath, string destPath, CancellationToken ct = default);

    /// <summary>
    /// Get the ownership of the parent directory
    /// </summary>
    Task<FileOwnership?> GetParentOwnershipAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Windows implementation of ownership provider using ACLs
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsOwnershipProvider : IOwnershipProvider
{
    private readonly ILogger<WindowsOwnershipProvider> _logger;

    public bool IsSupported => true;

    public WindowsOwnershipProvider(ILogger<WindowsOwnershipProvider> logger)
    {
        _logger = logger;
    }

    public async Task<FileOwnership?> GetOwnershipAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                var fileInfo = new FileInfo(path);
                var isFile = fileInfo.Exists;
                var isDir = !isFile && Directory.Exists(path);

                if (!isFile && !isDir)
                    return null;

                IdentityReference? owner;
                IdentityReference? group;

                if (isFile)
                {
                    var fileSecurity = fileInfo.GetAccessControl();
                    owner = fileSecurity.GetOwner(typeof(SecurityIdentifier));
                    group = fileSecurity.GetGroup(typeof(SecurityIdentifier));
                }
                else
                {
                    var dirSecurity = new DirectoryInfo(path).GetAccessControl();
                    owner = dirSecurity.GetOwner(typeof(SecurityIdentifier));
                    group = dirSecurity.GetGroup(typeof(SecurityIdentifier));
                }

                string? ownerName = null;
                string? groupName = null;

                try
                {
                    if (owner != null)
                    {
                        var account = (NTAccount?)owner.Translate(typeof(NTAccount));
                        ownerName = account?.Value;
                    }
                }
                catch
                {
                    // Ignore translation errors
                }

                try
                {
                    if (group != null)
                    {
                        var account = (NTAccount?)group.Translate(typeof(NTAccount));
                        groupName = account?.Value;
                    }
                }
                catch
                {
                    // Ignore translation errors
                }

                return new FileOwnership
                {
                    OwnerId = owner?.Value ?? string.Empty,
                    GroupId = group?.Value ?? string.Empty,
                    OwnerName = ownerName,
                    GroupName = groupName,
                    UnixMode = GetApproximateUnixMode(fileInfo.Exists ? fileInfo.Attributes : new DirectoryInfo(path).Attributes)
                };
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting ownership for: {Path}", path);
            return null;
        }
    }

    public async Task<bool> SetOwnershipAsync(string path, FileOwnership ownership, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                var fileInfo = new FileInfo(path);
                var isFile = fileInfo.Exists;
                var isDir = !isFile && Directory.Exists(path);

                if (!isFile && !isDir)
                    return false;

                if (isFile)
                {
                    var fileSecurity = fileInfo.GetAccessControl();

                    // Set owner
                    if (!string.IsNullOrEmpty(ownership.OwnerId))
                    {
                        var ownerSid = new SecurityIdentifier(ownership.OwnerId);
                        fileSecurity.SetOwner(ownerSid);
                    }

                    // Set group
                    if (!string.IsNullOrEmpty(ownership.GroupId))
                    {
                        var groupSid = new SecurityIdentifier(ownership.GroupId);
                        fileSecurity.SetGroup(groupSid);
                    }

                    fileInfo.SetAccessControl(fileSecurity);
                }
                else
                {
                    var dirInfo = new DirectoryInfo(path);
                    var dirSecurity = dirInfo.GetAccessControl();

                    // Set owner
                    if (!string.IsNullOrEmpty(ownership.OwnerId))
                    {
                        var ownerSid = new SecurityIdentifier(ownership.OwnerId);
                        dirSecurity.SetOwner(ownerSid);
                    }

                    // Set group
                    if (!string.IsNullOrEmpty(ownership.GroupId))
                    {
                        var groupSid = new SecurityIdentifier(ownership.GroupId);
                        dirSecurity.SetGroup(groupSid);
                    }

                    dirInfo.SetAccessControl(dirSecurity);
                }

                _logger.LogTrace("Set ownership on: {Path}", path);
                return true;
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error setting ownership for: {Path}", path);
            return false;
        }
    }

    public async Task<bool> CopyOwnershipAsync(string sourcePath, string destPath, CancellationToken ct = default)
    {
        var sourceOwnership = await GetOwnershipAsync(sourcePath, ct);
        if (sourceOwnership == null)
            return false;

        return await SetOwnershipAsync(destPath, sourceOwnership, ct);
    }

    public async Task<FileOwnership?> GetParentOwnershipAsync(string path, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
            return null;

        return await GetOwnershipAsync(parent, ct);
    }

    private static int GetApproximateUnixMode(FileAttributes attributes)
    {
        // Approximate Unix mode from Windows attributes
        // Note: Using decimal values (0o755 = 493, 0o644 = 420, 0o222 = 146)
        int mode;

        if ((attributes & FileAttributes.Directory) != 0)
        {
            mode = 493; // 0o755 = drwxr-xr-x
        }
        else
        {
            mode = 420; // 0o644 = -rw-r--r--
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            mode &= ~146; // ~0o222 = Remove write permissions
        }

        return mode;
    }
}

/// <summary>
/// Unix implementation of ownership provider using chown/chgrp
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class UnixOwnershipProvider : IOwnershipProvider
{
    private readonly ILogger<UnixOwnershipProvider> _logger;

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                                RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public UnixOwnershipProvider(ILogger<UnixOwnershipProvider> logger)
    {
        _logger = logger;
    }

    // Unix P/Invoke declarations
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int stat(string path, out StatBuf buf);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat(string path, out StatBuf buf);

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int chown(string path, int owner, int group);

    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    private static extern int lchown(string path, int owner, int group);

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int chmod(string path, int mode);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatBuf
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public int __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atime;
        public long st_atime_nsec;
        public long st_mtime;
        public long st_mtime_nsec;
        public long st_ctime;
        public long st_ctime_nsec;
        public long __unused1;
        public long __unused2;
        public long __unused3;
    }

    public async Task<FileOwnership?> GetOwnershipAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                if (stat(path, out var buf) != 0)
                {
                    _logger.LogWarning("stat failed for: {Path}, errno={Error}", path, Marshal.GetLastWin32Error());
                    return null;
                }

                return new FileOwnership
                {
                    OwnerId = buf.st_uid.ToString(),
                    GroupId = buf.st_gid.ToString(),
                    OwnerName = GetUserName(buf.st_uid),
                    GroupName = GetGroupName(buf.st_gid),
                    UnixMode = (int)(buf.st_mode & 4095) // 0o7777 = 4095
                };
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting ownership for: {Path}", path);
            return null;
        }
    }

    public async Task<bool> SetOwnershipAsync(string path, FileOwnership ownership, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                int uid = -1;
                int gid = -1;

                if (!string.IsNullOrEmpty(ownership.OwnerId) && int.TryParse(ownership.OwnerId, out var parsedUid))
                {
                    uid = parsedUid;
                }

                if (!string.IsNullOrEmpty(ownership.GroupId) && int.TryParse(ownership.GroupId, out var parsedGid))
                {
                    gid = parsedGid;
                }

                // Set owner/group
                if (uid >= 0 || gid >= 0)
                {
                    int result = chown(path, uid, gid);
                    if (result != 0)
                    {
                        _logger.LogWarning("chown failed for: {Path}, errno={Error}", path, Marshal.GetLastWin32Error());
                        return false;
                    }
                }

                // Set mode
                if (ownership.UnixMode > 0)
                {
                    int result = chmod(path, ownership.UnixMode);
                    if (result != 0)
                    {
                        _logger.LogWarning("chmod failed for: {Path}, errno={Error}", path, Marshal.GetLastWin32Error());
                        return false;
                    }
                }

                _logger.LogTrace("Set ownership on: {Path}, uid={Uid}, gid={Gid}, mode={Mode:o}",
                    path, uid, gid, ownership.UnixMode);
                return true;
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error setting ownership for: {Path}", path);
            return false;
        }
    }

    public async Task<bool> CopyOwnershipAsync(string sourcePath, string destPath, CancellationToken ct = default)
    {
        var sourceOwnership = await GetOwnershipAsync(sourcePath, ct);
        if (sourceOwnership == null)
            return false;

        return await SetOwnershipAsync(destPath, sourceOwnership, ct);
    }

    public async Task<FileOwnership?> GetParentOwnershipAsync(string path, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
            return null;

        return await GetOwnershipAsync(parent, ct);
    }

    [DllImport("libc", EntryPoint = "getpwuid")]
    private static extern IntPtr getpwuid(uint uid);

    [DllImport("libc", EntryPoint = "getgrgid")]
    private static extern IntPtr getgrgid(uint gid);

    private static string? GetUserName(uint uid)
    {
        try
        {
            var ptr = getpwuid(uid);
            if (ptr == IntPtr.Zero) return null;

            // First field of passwd struct is pw_name (char*)
            var namePtr = Marshal.ReadIntPtr(ptr);
            return Marshal.PtrToStringAnsi(namePtr);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetGroupName(uint gid)
    {
        try
        {
            var ptr = getgrgid(gid);
            if (ptr == IntPtr.Zero) return null;

            // First field of group struct is gr_name (char*)
            var namePtr = Marshal.ReadIntPtr(ptr);
            return Marshal.PtrToStringAnsi(namePtr);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Factory for creating the appropriate ownership provider based on platform
/// </summary>
public class OwnershipProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public OwnershipProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IOwnershipProvider Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsOwnershipProvider(
                _loggerFactory.CreateLogger<WindowsOwnershipProvider>());
        }
        else
        {
            // CA1416: This is intentionally called only on non-Windows platforms
            // The RuntimeInformation check above ensures this code path is only taken on Unix
#pragma warning disable CA1416
            return new UnixOwnershipProvider(
                _loggerFactory.CreateLogger<UnixOwnershipProvider>());
#pragma warning restore CA1416
        }
    }
}
