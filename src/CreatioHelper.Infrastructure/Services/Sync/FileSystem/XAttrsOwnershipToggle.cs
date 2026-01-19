using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Middleware for controlling sync of extended attributes and ownership information.
/// Based on Syncthing's SyncXattrs, SendXattrs, SyncOwnership, SendOwnership configurations.
/// </summary>
public interface IXAttrsOwnershipToggle
{
    /// <summary>
    /// Check if extended attributes should be synced for a folder.
    /// </summary>
    bool ShouldSyncXAttrs(SyncFolder folder);

    /// <summary>
    /// Check if extended attributes should be sent to remote for a folder.
    /// </summary>
    bool ShouldSendXAttrs(SyncFolder folder);

    /// <summary>
    /// Check if ownership should be synced for a folder.
    /// </summary>
    bool ShouldSyncOwnership(SyncFolder folder);

    /// <summary>
    /// Check if ownership should be sent to remote for a folder.
    /// </summary>
    bool ShouldSendOwnership(SyncFolder folder);

    /// <summary>
    /// Filter extended attributes based on folder configuration.
    /// </summary>
    Task<Dictionary<string, byte[]>> FilterXAttrsAsync(
        SyncFolder folder,
        Dictionary<string, byte[]> xattrs,
        XAttrDirection direction,
        CancellationToken ct = default);

    /// <summary>
    /// Filter ownership information based on folder configuration.
    /// </summary>
    OwnershipInfo? FilterOwnership(
        SyncFolder folder,
        OwnershipInfo? ownership,
        XAttrDirection direction);
}

/// <summary>
/// Direction of attribute sync (receiving or sending).
/// </summary>
public enum XAttrDirection
{
    /// <summary>Receiving attributes from remote</summary>
    Receive,

    /// <summary>Sending attributes to remote</summary>
    Send
}

/// <summary>
/// Ownership information for a file.
/// </summary>
public record OwnershipInfo(
    int? Uid,
    int? Gid,
    string? User,
    string? Group);

/// <summary>
/// Configuration for xattr filtering.
/// </summary>
public class XAttrFilterConfiguration
{
    /// <summary>
    /// Patterns for attributes to include (empty = include all).
    /// </summary>
    public List<string> IncludePatterns { get; set; } = new();

    /// <summary>
    /// Patterns for attributes to exclude.
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "system.*",        // Windows system attributes
        "security.selinux", // SELinux contexts
        "com.apple.quarantine", // macOS quarantine
    };

    /// <summary>
    /// Maximum size of a single xattr value in bytes.
    /// </summary>
    public int MaxXAttrValueSize { get; set; } = 64 * 1024; // 64KB

    /// <summary>
    /// Maximum total size of all xattrs for a file in bytes.
    /// </summary>
    public int MaxTotalXAttrSize { get; set; } = 1024 * 1024; // 1MB
}

/// <summary>
/// Implementation of xattrs/ownership toggle.
/// </summary>
public class XAttrsOwnershipToggle : IXAttrsOwnershipToggle
{
    private readonly ILogger<XAttrsOwnershipToggle> _logger;
    private readonly XAttrFilterConfiguration _config;

    public XAttrsOwnershipToggle(
        ILogger<XAttrsOwnershipToggle> logger,
        XAttrFilterConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new XAttrFilterConfiguration();
    }

    /// <inheritdoc />
    public bool ShouldSyncXAttrs(SyncFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        // Only sync xattrs if the folder is configured to receive them
        var sync = folder.SyncXattrs;

        _logger.LogTrace(
            "ShouldSyncXAttrs for folder {FolderId}: {Result}",
            folder.Id, sync);

        return sync;
    }

    /// <inheritdoc />
    public bool ShouldSendXAttrs(SyncFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        // Only send xattrs if the folder is configured to send them
        var send = folder.SendXattrs;

        _logger.LogTrace(
            "ShouldSendXAttrs for folder {FolderId}: {Result}",
            folder.Id, send);

        return send;
    }

    /// <inheritdoc />
    public bool ShouldSyncOwnership(SyncFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        // Only sync ownership if the folder is configured to receive it
        var sync = folder.SyncOwnership;

        _logger.LogTrace(
            "ShouldSyncOwnership for folder {FolderId}: {Result}",
            folder.Id, sync);

        return sync;
    }

    /// <inheritdoc />
    public bool ShouldSendOwnership(SyncFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        // Only send ownership if the folder is configured to send it
        var send = folder.SendOwnership;

        _logger.LogTrace(
            "ShouldSendOwnership for folder {FolderId}: {Result}",
            folder.Id, send);

        return send;
    }

    /// <inheritdoc />
    public Task<Dictionary<string, byte[]>> FilterXAttrsAsync(
        SyncFolder folder,
        Dictionary<string, byte[]> xattrs,
        XAttrDirection direction,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (xattrs == null || xattrs.Count == 0)
        {
            return Task.FromResult(new Dictionary<string, byte[]>());
        }

        // Check if xattrs should be processed for this direction
        var shouldProcess = direction switch
        {
            XAttrDirection.Receive => ShouldSyncXAttrs(folder),
            XAttrDirection.Send => ShouldSendXAttrs(folder),
            _ => false
        };

        if (!shouldProcess)
        {
            _logger.LogDebug(
                "XAttrs disabled for folder {FolderId} (direction: {Direction})",
                folder.Id, direction);
            return Task.FromResult(new Dictionary<string, byte[]>());
        }

        var filtered = new Dictionary<string, byte[]>();
        var totalSize = 0L;

        foreach (var (name, value) in xattrs)
        {
            ct.ThrowIfCancellationRequested();

            // Check exclusion patterns
            if (IsExcluded(name))
            {
                _logger.LogTrace("XAttr {Name} excluded by pattern", name);
                continue;
            }

            // Check inclusion patterns (if any are defined)
            if (_config.IncludePatterns.Count > 0 && !IsIncluded(name))
            {
                _logger.LogTrace("XAttr {Name} not included by pattern", name);
                continue;
            }

            // Check size limits
            if (value.Length > _config.MaxXAttrValueSize)
            {
                _logger.LogWarning(
                    "XAttr {Name} exceeds max size ({Size} > {Max}), skipping",
                    name, value.Length, _config.MaxXAttrValueSize);
                continue;
            }

            if (totalSize + value.Length > _config.MaxTotalXAttrSize)
            {
                _logger.LogWarning(
                    "Total xattr size would exceed limit ({Total} + {Size} > {Max}), stopping",
                    totalSize, value.Length, _config.MaxTotalXAttrSize);
                break;
            }

            filtered[name] = value;
            totalSize += value.Length;
        }

        _logger.LogDebug(
            "Filtered xattrs for folder {FolderId}: {Original} -> {Filtered} attributes",
            folder.Id, xattrs.Count, filtered.Count);

        return Task.FromResult(filtered);
    }

    /// <inheritdoc />
    public OwnershipInfo? FilterOwnership(
        SyncFolder folder,
        OwnershipInfo? ownership,
        XAttrDirection direction)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (ownership == null)
        {
            return null;
        }

        // Check if ownership should be processed for this direction
        var shouldProcess = direction switch
        {
            XAttrDirection.Receive => ShouldSyncOwnership(folder),
            XAttrDirection.Send => ShouldSendOwnership(folder),
            _ => false
        };

        if (!shouldProcess)
        {
            _logger.LogDebug(
                "Ownership disabled for folder {FolderId} (direction: {Direction})",
                folder.Id, direction);
            return null;
        }

        return ownership;
    }

    private bool IsExcluded(string name)
    {
        foreach (var pattern in _config.ExcludePatterns)
        {
            if (MatchesPattern(name, pattern))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsIncluded(string name)
    {
        foreach (var pattern in _config.IncludePatterns)
        {
            if (MatchesPattern(name, pattern))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        // Simple wildcard matching
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.StartsWith('*'))
        {
            var suffix = pattern[1..];
            return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
        return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Decorator that provides caching for xattr filter decisions.
/// </summary>
public class CachedXAttrsOwnershipToggle : IXAttrsOwnershipToggle
{
    private readonly IXAttrsOwnershipToggle _inner;
    private readonly Dictionary<string, bool> _syncXAttrsCache = new();
    private readonly Dictionary<string, bool> _sendXAttrsCache = new();
    private readonly Dictionary<string, bool> _syncOwnershipCache = new();
    private readonly Dictionary<string, bool> _sendOwnershipCache = new();

    public CachedXAttrsOwnershipToggle(IXAttrsOwnershipToggle inner)
    {
        _inner = inner;
    }

    public bool ShouldSyncXAttrs(SyncFolder folder)
    {
        if (!_syncXAttrsCache.TryGetValue(folder.Id, out var result))
        {
            result = _inner.ShouldSyncXAttrs(folder);
            _syncXAttrsCache[folder.Id] = result;
        }
        return result;
    }

    public bool ShouldSendXAttrs(SyncFolder folder)
    {
        if (!_sendXAttrsCache.TryGetValue(folder.Id, out var result))
        {
            result = _inner.ShouldSendXAttrs(folder);
            _sendXAttrsCache[folder.Id] = result;
        }
        return result;
    }

    public bool ShouldSyncOwnership(SyncFolder folder)
    {
        if (!_syncOwnershipCache.TryGetValue(folder.Id, out var result))
        {
            result = _inner.ShouldSyncOwnership(folder);
            _syncOwnershipCache[folder.Id] = result;
        }
        return result;
    }

    public bool ShouldSendOwnership(SyncFolder folder)
    {
        if (!_sendOwnershipCache.TryGetValue(folder.Id, out var result))
        {
            result = _inner.ShouldSendOwnership(folder);
            _sendOwnershipCache[folder.Id] = result;
        }
        return result;
    }

    public Task<Dictionary<string, byte[]>> FilterXAttrsAsync(
        SyncFolder folder, Dictionary<string, byte[]> xattrs, XAttrDirection direction, CancellationToken ct = default)
        => _inner.FilterXAttrsAsync(folder, xattrs, direction, ct);

    public OwnershipInfo? FilterOwnership(SyncFolder folder, OwnershipInfo? ownership, XAttrDirection direction)
        => _inner.FilterOwnership(folder, ownership, direction);

    /// <summary>
    /// Invalidate cached decisions for a folder.
    /// </summary>
    public void InvalidateCache(string folderId)
    {
        _syncXAttrsCache.Remove(folderId);
        _sendXAttrsCache.Remove(folderId);
        _syncOwnershipCache.Remove(folderId);
        _sendOwnershipCache.Remove(folderId);
    }

    /// <summary>
    /// Clear all cached decisions.
    /// </summary>
    public void ClearCache()
    {
        _syncXAttrsCache.Clear();
        _sendXAttrsCache.Clear();
        _syncOwnershipCache.Clear();
        _sendOwnershipCache.Clear();
    }
}
