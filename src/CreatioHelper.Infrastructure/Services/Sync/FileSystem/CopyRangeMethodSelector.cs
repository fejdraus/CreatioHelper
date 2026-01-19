using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

// Note: CopyRangeMethod enum is defined in CopyRangeOptimizer.cs

/// <summary>
/// Service for selecting and executing the optimal copy range method.
/// </summary>
public interface ICopyRangeMethodSelector
{
    /// <summary>
    /// Get the best available copy method for the current platform.
    /// </summary>
    CopyRangeMethod GetBestMethod();

    /// <summary>
    /// Get the best method for a specific filesystem.
    /// </summary>
    CopyRangeMethod GetBestMethodForFilesystem(string filesystemType);

    /// <summary>
    /// Check if a specific method is supported on the current platform.
    /// </summary>
    bool IsMethodSupported(CopyRangeMethod method);

    /// <summary>
    /// Get information about a copy method.
    /// </summary>
    CopyMethodInfo GetMethodInfo(CopyRangeMethod method);

    /// <summary>
    /// Get all supported methods in order of preference.
    /// </summary>
    IReadOnlyList<CopyRangeMethod> GetSupportedMethods();

    /// <summary>
    /// Test if reflink is supported at a path.
    /// </summary>
    bool TestReflinkSupport(string path);
}

/// <summary>
/// Information about a copy method.
/// </summary>
public record CopyMethodInfo
{
    public CopyRangeMethod Method { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsSupported { get; init; }
    public bool RequiresKernelSupport { get; init; }
    public bool RequiresFilesystemSupport { get; init; }
    public string[] SupportedFilesystems { get; init; } = Array.Empty<string>();
    public string[] SupportedPlatforms { get; init; } = Array.Empty<string>();
    public bool IsCoW { get; init; } // Copy-on-Write
    public bool PreservesHoles { get; init; } // Sparse file support
}

/// <summary>
/// Implementation of copy range method selector.
/// </summary>
public class CopyRangeMethodSelector : ICopyRangeMethodSelector
{
    private readonly ILogger<CopyRangeMethodSelector> _logger;
    private readonly bool _isWindows;
    private readonly bool _isLinux;
    private readonly bool _isMacOS;

    private static readonly Dictionary<CopyRangeMethod, CopyMethodInfo> MethodInfos = new()
    {
        [CopyRangeMethod.Standard] = new CopyMethodInfo
        {
            Method = CopyRangeMethod.Standard,
            Name = "Standard",
            Description = "Standard read/write copy using user-space buffers",
            IsSupported = true,
            RequiresKernelSupport = false,
            RequiresFilesystemSupport = false,
            SupportedPlatforms = new[] { "Windows", "Linux", "macOS" },
            IsCoW = false,
            PreservesHoles = false
        },
        [CopyRangeMethod.CopyFileRange] = new CopyMethodInfo
        {
            Method = CopyRangeMethod.CopyFileRange,
            Name = "copy_file_range",
            Description = "Linux copy_file_range syscall for kernel-optimized copying",
            RequiresKernelSupport = true,
            RequiresFilesystemSupport = false,
            SupportedPlatforms = new[] { "Linux" },
            IsCoW = true, // Can be CoW if filesystem supports it
            PreservesHoles = true
        },
        [CopyRangeMethod.Reflink] = new CopyMethodInfo
        {
            Method = CopyRangeMethod.Reflink,
            Name = "Reflink (CoW)",
            Description = "Copy-on-Write reflink using FICLONE ioctl",
            RequiresKernelSupport = true,
            RequiresFilesystemSupport = true,
            SupportedFilesystems = new[] { "btrfs", "xfs", "ocfs2", "apfs" },
            SupportedPlatforms = new[] { "Linux", "macOS" },
            IsCoW = true,
            PreservesHoles = true
        },
        [CopyRangeMethod.SendFile] = new CopyMethodInfo
        {
            Method = CopyRangeMethod.SendFile,
            Name = "sendfile",
            Description = "Linux sendfile syscall for zero-copy between file descriptors",
            RequiresKernelSupport = true,
            RequiresFilesystemSupport = false,
            SupportedPlatforms = new[] { "Linux" },
            IsCoW = false,
            PreservesHoles = false
        },
        [CopyRangeMethod.WindowsCopy] = new CopyMethodInfo
        {
            Method = CopyRangeMethod.WindowsCopy,
            Name = "Windows CopyFileEx",
            Description = "Windows native file copy API",
            RequiresKernelSupport = false,
            RequiresFilesystemSupport = false,
            SupportedPlatforms = new[] { "Windows" },
            IsCoW = false,
            PreservesHoles = true
        },
        [CopyRangeMethod.DuplicateExtents] = new CopyMethodInfo
        {
            Method = CopyRangeMethod.DuplicateExtents,
            Name = "Duplicate Extents",
            Description = "Windows block cloning for ReFS and Dev Drive NTFS",
            RequiresKernelSupport = true,
            RequiresFilesystemSupport = true,
            SupportedFilesystems = new[] { "refs", "ntfs" },
            SupportedPlatforms = new[] { "Windows" },
            IsCoW = true,
            PreservesHoles = true
        },
        [CopyRangeMethod.MemoryMapped] = new CopyMethodInfo
        {
            Method = CopyRangeMethod.MemoryMapped,
            Name = "Memory Mapped",
            Description = "Copy using memory-mapped files",
            RequiresKernelSupport = false,
            RequiresFilesystemSupport = false,
            SupportedPlatforms = new[] { "Windows", "Linux", "macOS" },
            IsCoW = false,
            PreservesHoles = false
        },
        [CopyRangeMethod.Auto] = new CopyMethodInfo
        {
            Method = CopyRangeMethod.Auto,
            Name = "Auto",
            Description = "Automatically select the best available method",
            IsSupported = true,
            RequiresKernelSupport = false,
            RequiresFilesystemSupport = false,
            SupportedPlatforms = new[] { "Windows", "Linux", "macOS" },
            IsCoW = false,
            PreservesHoles = false
        }
    };

    public CopyRangeMethodSelector(ILogger<CopyRangeMethodSelector> logger)
    {
        _logger = logger;
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        _isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }

    /// <inheritdoc />
    public CopyRangeMethod GetBestMethod()
    {
        if (_isLinux)
        {
            // Try reflink first (most efficient if supported)
            // Then copy_file_range, then sendfile, then standard
            return CopyRangeMethod.CopyFileRange;
        }

        if (_isWindows)
        {
            return CopyRangeMethod.WindowsCopy;
        }

        if (_isMacOS)
        {
            // macOS APFS supports clonefile
            return CopyRangeMethod.Reflink;
        }

        return CopyRangeMethod.Standard;
    }

    /// <inheritdoc />
    public CopyRangeMethod GetBestMethodForFilesystem(string filesystemType)
    {
        if (string.IsNullOrEmpty(filesystemType))
        {
            return GetBestMethod();
        }

        var fs = filesystemType.ToLowerInvariant();

        // CoW filesystems - use reflink
        if (fs is "btrfs" or "xfs" or "ocfs2" or "apfs" or "refs")
        {
            return CopyRangeMethod.Reflink;
        }

        // Windows NTFS with Dev Drive
        if (fs == "ntfs" && _isWindows)
        {
            // Could check for Dev Drive support
            return CopyRangeMethod.WindowsCopy;
        }

        // Network filesystems - use standard (most compatible)
        if (fs is "nfs" or "cifs" or "smb" or "sshfs")
        {
            return CopyRangeMethod.Standard;
        }

        return GetBestMethod();
    }

    /// <inheritdoc />
    public bool IsMethodSupported(CopyRangeMethod method)
    {
        if (method == CopyRangeMethod.Auto || method == CopyRangeMethod.Standard)
        {
            return true;
        }

        if (!MethodInfos.TryGetValue(method, out var info))
        {
            return false;
        }

        var currentPlatform = _isWindows ? "Windows" : _isLinux ? "Linux" : _isMacOS ? "macOS" : "Unknown";
        return info.SupportedPlatforms.Contains(currentPlatform, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public CopyMethodInfo GetMethodInfo(CopyRangeMethod method)
    {
        if (MethodInfos.TryGetValue(method, out var info))
        {
            return info with { IsSupported = IsMethodSupported(method) };
        }

        return new CopyMethodInfo
        {
            Method = method,
            Name = method.ToString(),
            Description = "Unknown method",
            IsSupported = false
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<CopyRangeMethod> GetSupportedMethods()
    {
        var methods = new List<CopyRangeMethod>();

        if (_isLinux)
        {
            methods.Add(CopyRangeMethod.Reflink);
            methods.Add(CopyRangeMethod.CopyFileRange);
            methods.Add(CopyRangeMethod.SendFile);
        }
        else if (_isWindows)
        {
            methods.Add(CopyRangeMethod.DuplicateExtents);
            methods.Add(CopyRangeMethod.WindowsCopy);
        }
        else if (_isMacOS)
        {
            methods.Add(CopyRangeMethod.Reflink);
        }

        methods.Add(CopyRangeMethod.MemoryMapped);
        methods.Add(CopyRangeMethod.Standard);

        return methods;
    }

    /// <inheritdoc />
    public bool TestReflinkSupport(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            // This would need platform-specific implementation
            // For now, return false (would need to actually try a reflink)
            _logger.LogDebug("Testing reflink support at {Path}", path);

            if (_isLinux)
            {
                // Would need to try FICLONE ioctl
                return false;
            }

            if (_isMacOS)
            {
                // Would need to try clonefile
                return false;
            }

            if (_isWindows)
            {
                // Would need to try FSCTL_DUPLICATE_EXTENTS_TO_FILE
                return false;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reflink not supported at {Path}", path);
            return false;
        }
    }
}

/// <summary>
/// Configuration for copy range method.
/// </summary>
public class CopyRangeConfiguration
{
    /// <summary>
    /// Preferred copy method. Default is Auto.
    /// </summary>
    public CopyRangeMethod PreferredMethod { get; set; } = CopyRangeMethod.Auto;

    /// <summary>
    /// Fallback method if preferred is not available.
    /// </summary>
    public CopyRangeMethod FallbackMethod { get; set; } = CopyRangeMethod.Standard;

    /// <summary>
    /// Enable reflink detection and usage.
    /// </summary>
    public bool EnableReflink { get; set; } = true;

    /// <summary>
    /// Minimum file size to use optimized copy methods (bytes).
    /// Smaller files use standard copy.
    /// </summary>
    public long MinFileSizeForOptimizedCopy { get; set; } = 64 * 1024; // 64 KB

    /// <summary>
    /// Per-folder method overrides.
    /// </summary>
    public Dictionary<string, CopyRangeMethod> FolderMethods { get; } = new();
}
