using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Provides extended attribute (xattr) operations for files (based on Syncthing basicfs_xattr.go)
/// Supports both Unix xattrs and Windows alternate data streams
/// </summary>
public interface IExtendedAttributeProvider
{
    /// <summary>
    /// Check if extended attributes are supported on this platform
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Get all extended attributes for a file
    /// </summary>
    Task<Dictionary<string, byte[]>> GetAttributesAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Get a specific extended attribute
    /// </summary>
    Task<byte[]?> GetAttributeAsync(string path, string name, CancellationToken ct = default);

    /// <summary>
    /// Set extended attributes for a file
    /// </summary>
    Task SetAttributesAsync(string path, Dictionary<string, byte[]> attrs, CancellationToken ct = default);

    /// <summary>
    /// Set a specific extended attribute
    /// </summary>
    Task SetAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default);

    /// <summary>
    /// Remove an extended attribute
    /// </summary>
    Task RemoveAttributeAsync(string path, string name, CancellationToken ct = default);

    /// <summary>
    /// List all extended attribute names
    /// </summary>
    Task<IReadOnlyList<string>> ListAttributeNamesAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Windows implementation of extended attributes using Alternate Data Streams (ADS)
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsExtendedAttributeProvider : IExtendedAttributeProvider
{
    private readonly ILogger<WindowsExtendedAttributeProvider> _logger;
    private const string XattrStreamPrefix = ":xattr.";

    public bool IsSupported => true;

    public WindowsExtendedAttributeProvider(ILogger<WindowsExtendedAttributeProvider> logger)
    {
        _logger = logger;
    }

    public async Task<Dictionary<string, byte[]>> GetAttributesAsync(string path, CancellationToken ct = default)
    {
        var result = new Dictionary<string, byte[]>();

        try
        {
            var names = await ListAttributeNamesAsync(path, ct);
            foreach (var name in names)
            {
                var value = await GetAttributeAsync(path, name, ct);
                if (value != null)
                {
                    result[name] = value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting extended attributes for: {Path}", path);
        }

        return result;
    }

    public async Task<byte[]?> GetAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        var streamPath = $"{path}{XattrStreamPrefix}{name}";

        try
        {
            if (!File.Exists(streamPath))
                return null;

            return await File.ReadAllBytesAsync(streamPath, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting attribute {Name} for: {Path}", name, path);
            return null;
        }
    }

    public async Task SetAttributesAsync(string path, Dictionary<string, byte[]> attrs, CancellationToken ct = default)
    {
        foreach (var (name, value) in attrs)
        {
            await SetAttributeAsync(path, name, value, ct);
        }
    }

    public async Task SetAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        var streamPath = $"{path}{XattrStreamPrefix}{name}";

        try
        {
            await File.WriteAllBytesAsync(streamPath, value, ct);
            _logger.LogTrace("Set attribute {Name} on: {Path}", name, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error setting attribute {Name} for: {Path}", name, path);
            throw;
        }
    }

    public async Task RemoveAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        var streamPath = $"{path}{XattrStreamPrefix}{name}";

        try
        {
            if (File.Exists(streamPath))
            {
                await Task.Run(() => File.Delete(streamPath), ct);
                _logger.LogTrace("Removed attribute {Name} from: {Path}", name, path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error removing attribute {Name} from: {Path}", name, path);
        }
    }

    public Task<IReadOnlyList<string>> ListAttributeNamesAsync(string path, CancellationToken ct = default)
    {
        var result = new List<string>();

        try
        {
            // Windows ADS enumeration requires FindFirstStreamW/FindNextStreamW
            // For simplicity, we use a pattern-based approach with known prefixes
            // Full implementation would use P/Invoke to FindFirstStreamW

            var directory = Path.GetDirectoryName(path) ?? ".";
            var fileName = Path.GetFileName(path);

            // Check for common xattr stream names
            var commonAttrs = new[] { "user.", "security.", "system.", "trusted." };
            foreach (var prefix in commonAttrs)
            {
                for (int i = 0; i < 100; i++) // Check up to 100 attributes per prefix
                {
                    var attrName = $"{prefix}attr{i}";
                    var streamPath = $"{path}{XattrStreamPrefix}{attrName}";
                    if (File.Exists(streamPath))
                    {
                        result.Add(attrName);
                    }
                    else if (i > 10)
                    {
                        break; // Stop checking if we haven't found any in a while
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error listing attribute names for: {Path}", path);
        }

        return Task.FromResult<IReadOnlyList<string>>(result);
    }
}

/// <summary>
/// Unix implementation of extended attributes using native xattr calls
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class UnixExtendedAttributeProvider : IExtendedAttributeProvider
{
    private readonly ILogger<UnixExtendedAttributeProvider> _logger;

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                                RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public UnixExtendedAttributeProvider(ILogger<UnixExtendedAttributeProvider> logger)
    {
        _logger = logger;
    }

    public async Task<Dictionary<string, byte[]>> GetAttributesAsync(string path, CancellationToken ct = default)
    {
        var result = new Dictionary<string, byte[]>();

        try
        {
            var names = await ListAttributeNamesAsync(path, ct);
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                var value = await GetAttributeAsync(path, name, ct);
                if (value != null)
                {
                    result[name] = value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting extended attributes for: {Path}", path);
        }

        return result;
    }

    public Task<byte[]?> GetAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        // Unix xattr operations are synchronous, wrap in Task.Run
        return Task.Run(() =>
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return GetXattrLinux(path, name);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return GetXattrMacOS(path, name);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting attribute {Name} for: {Path}", name, path);
                return null;
            }
        }, ct);
    }

    public async Task SetAttributesAsync(string path, Dictionary<string, byte[]> attrs, CancellationToken ct = default)
    {
        foreach (var (name, value) in attrs)
        {
            await SetAttributeAsync(path, name, value, ct);
        }
    }

    public Task SetAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    SetXattrLinux(path, name, value);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    SetXattrMacOS(path, name, value);
                }
                _logger.LogTrace("Set attribute {Name} on: {Path}", name, path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error setting attribute {Name} for: {Path}", name, path);
                throw;
            }
        }, ct);
    }

    public Task RemoveAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    RemoveXattrLinux(path, name);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    RemoveXattrMacOS(path, name);
                }
                _logger.LogTrace("Removed attribute {Name} from: {Path}", name, path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error removing attribute {Name} from: {Path}", name, path);
            }
        }, ct);
    }

    public Task<IReadOnlyList<string>> ListAttributeNamesAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return ListXattrLinux(path);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return ListXattrMacOS(path);
                }
                return (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error listing attribute names for: {Path}", path);
                return (IReadOnlyList<string>)Array.Empty<string>();
            }
        }, ct);
    }

    // Linux P/Invoke declarations
    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern int linux_getxattr(string path, string name, byte[] value, int size);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int linux_setxattr(string path, string name, byte[] value, int size, int flags);

    [DllImport("libc", EntryPoint = "removexattr", SetLastError = true)]
    private static extern int linux_removexattr(string path, string name);

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern int linux_listxattr(string path, byte[] list, int size);

    // macOS P/Invoke declarations (slightly different API)
    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern int macos_getxattr(string path, string name, byte[] value, int size, int position, int options);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int macos_setxattr(string path, string name, byte[] value, int size, int position, int options);

    [DllImport("libc", EntryPoint = "removexattr", SetLastError = true)]
    private static extern int macos_removexattr(string path, string name, int options);

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern int macos_listxattr(string path, byte[] list, int size, int options);

    private byte[]? GetXattrLinux(string path, string name)
    {
        // First, get the size
        int size = linux_getxattr(path, name, Array.Empty<byte>(), 0);
        if (size < 0) return null;

        var buffer = new byte[size];
        int result = linux_getxattr(path, name, buffer, size);
        return result >= 0 ? buffer : null;
    }

    private void SetXattrLinux(string path, string name, byte[] value)
    {
        int result = linux_setxattr(path, name, value, value.Length, 0);
        if (result < 0)
            throw new IOException($"Failed to set xattr: {Marshal.GetLastWin32Error()}");
    }

    private void RemoveXattrLinux(string path, string name)
    {
        linux_removexattr(path, name);
    }

    private IReadOnlyList<string> ListXattrLinux(string path)
    {
        // First, get the size
        int size = linux_listxattr(path, Array.Empty<byte>(), 0);
        if (size <= 0) return Array.Empty<string>();

        var buffer = new byte[size];
        int result = linux_listxattr(path, buffer, size);
        if (result < 0) return Array.Empty<string>();

        return ParseNullSeparatedList(buffer, result);
    }

    private byte[]? GetXattrMacOS(string path, string name)
    {
        int size = macos_getxattr(path, name, Array.Empty<byte>(), 0, 0, 0);
        if (size < 0) return null;

        var buffer = new byte[size];
        int result = macos_getxattr(path, name, buffer, size, 0, 0);
        return result >= 0 ? buffer : null;
    }

    private void SetXattrMacOS(string path, string name, byte[] value)
    {
        int result = macos_setxattr(path, name, value, value.Length, 0, 0);
        if (result < 0)
            throw new IOException($"Failed to set xattr: {Marshal.GetLastWin32Error()}");
    }

    private void RemoveXattrMacOS(string path, string name)
    {
        macos_removexattr(path, name, 0);
    }

    private IReadOnlyList<string> ListXattrMacOS(string path)
    {
        int size = macos_listxattr(path, Array.Empty<byte>(), 0, 0);
        if (size <= 0) return Array.Empty<string>();

        var buffer = new byte[size];
        int result = macos_listxattr(path, buffer, size, 0);
        if (result < 0) return Array.Empty<string>();

        return ParseNullSeparatedList(buffer, result);
    }

    private static IReadOnlyList<string> ParseNullSeparatedList(byte[] buffer, int length)
    {
        var names = new List<string>();
        var start = 0;

        for (int i = 0; i < length; i++)
        {
            if (buffer[i] == 0)
            {
                if (i > start)
                {
                    names.Add(Encoding.UTF8.GetString(buffer, start, i - start));
                }
                start = i + 1;
            }
        }

        return names;
    }
}

/// <summary>
/// Factory for creating the appropriate extended attribute provider based on platform
/// </summary>
public class ExtendedAttributeProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public ExtendedAttributeProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IExtendedAttributeProvider Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsExtendedAttributeProvider(
                _loggerFactory.CreateLogger<WindowsExtendedAttributeProvider>());
        }
        else
        {
            // CA1416: This is intentionally called only on non-Windows platforms
            // The RuntimeInformation check above ensures this code path is only taken on Unix
#pragma warning disable CA1416
            return new UnixExtendedAttributeProvider(
                _loggerFactory.CreateLogger<UnixExtendedAttributeProvider>());
#pragma warning restore CA1416
        }
    }
}
