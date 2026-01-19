using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Service for managing folder marker file names.
/// Based on Syncthing's .stfolder marker functionality.
/// </summary>
public interface IMarkerNameService
{
    /// <summary>
    /// Get the marker name for a folder.
    /// </summary>
    string GetMarkerName(string folderId);

    /// <summary>
    /// Set custom marker name for a folder.
    /// </summary>
    void SetMarkerName(string folderId, string markerName);

    /// <summary>
    /// Reset marker name to default for a folder.
    /// </summary>
    void ResetMarkerName(string folderId);

    /// <summary>
    /// Check if a path is a marker file.
    /// </summary>
    bool IsMarkerFile(string folderId, string path);

    /// <summary>
    /// Get the full marker path for a folder.
    /// </summary>
    string GetMarkerPath(string folderId, string folderPath);

    /// <summary>
    /// Ensure marker exists in folder.
    /// </summary>
    bool EnsureMarkerExists(string folderId, string folderPath);

    /// <summary>
    /// Check if marker exists in folder.
    /// </summary>
    bool MarkerExists(string folderId, string folderPath);

    /// <summary>
    /// Validate marker name.
    /// </summary>
    bool IsValidMarkerName(string markerName);

    /// <summary>
    /// Get all configured marker names.
    /// </summary>
    IReadOnlyDictionary<string, string> GetAllMarkerNames();
}

/// <summary>
/// Configuration for marker names.
/// </summary>
public class MarkerNameConfiguration
{
    /// <summary>
    /// Default marker name (Syncthing uses .stfolder).
    /// </summary>
    public string DefaultMarkerName { get; set; } = ".stfolder";

    /// <summary>
    /// Per-folder marker name overrides.
    /// </summary>
    public Dictionary<string, string> FolderMarkerNames { get; } = new();

    /// <summary>
    /// Characters not allowed in marker names.
    /// </summary>
    public char[] InvalidCharacters { get; set; } = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Maximum marker name length.
    /// </summary>
    public int MaxMarkerNameLength { get; set; } = 255;

    /// <summary>
    /// Whether marker should be hidden (start with dot).
    /// </summary>
    public bool PreferHiddenMarker { get; set; } = true;

    /// <summary>
    /// Create marker as directory instead of file.
    /// </summary>
    public bool CreateAsDirectory { get; set; } = true;
}

/// <summary>
/// Statistics about marker operations.
/// </summary>
public class MarkerStats
{
    public string FolderId { get; set; } = string.Empty;
    public string MarkerName { get; set; } = string.Empty;
    public bool MarkerExists { get; set; }
    public DateTime? LastChecked { get; set; }
    public DateTime? MarkerCreatedAt { get; set; }
    public int CheckCount { get; set; }
    public int CreateCount { get; set; }
}

/// <summary>
/// Implementation of marker name service.
/// </summary>
public class MarkerNameService : IMarkerNameService
{
    private readonly ILogger<MarkerNameService> _logger;
    private readonly MarkerNameConfiguration _config;
    private readonly ConcurrentDictionary<string, string> _markerNames = new();
    private readonly ConcurrentDictionary<string, MarkerStats> _stats = new();

    public MarkerNameService(
        ILogger<MarkerNameService> logger,
        MarkerNameConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new MarkerNameConfiguration();

        // Initialize from config
        foreach (var kvp in _config.FolderMarkerNames)
        {
            _markerNames[kvp.Key] = kvp.Value;
        }
    }

    /// <inheritdoc />
    public string GetMarkerName(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_markerNames.TryGetValue(folderId, out var customName))
        {
            return customName;
        }

        return _config.DefaultMarkerName;
    }

    /// <inheritdoc />
    public void SetMarkerName(string folderId, string markerName)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(markerName);

        if (!IsValidMarkerName(markerName))
        {
            throw new ArgumentException($"Invalid marker name: {markerName}", nameof(markerName));
        }

        _markerNames[folderId] = markerName;
        _config.FolderMarkerNames[folderId] = markerName;

        _logger.LogInformation("Set marker name for folder {FolderId}: {MarkerName}",
            folderId, markerName);
    }

    /// <inheritdoc />
    public void ResetMarkerName(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        _markerNames.TryRemove(folderId, out _);
        _config.FolderMarkerNames.Remove(folderId);

        _logger.LogInformation("Reset marker name for folder {FolderId} to default", folderId);
    }

    /// <inheritdoc />
    public bool IsMarkerFile(string folderId, string path)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(path);

        var markerName = GetMarkerName(folderId);
        var fileName = Path.GetFileName(path);

        return string.Equals(fileName, markerName, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string GetMarkerPath(string folderId, string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(folderPath);

        var markerName = GetMarkerName(folderId);
        return Path.Combine(folderPath, markerName);
    }

    /// <inheritdoc />
    public bool EnsureMarkerExists(string folderId, string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(folderPath);

        var markerPath = GetMarkerPath(folderId, folderPath);
        var stats = GetOrCreateStats(folderId);

        try
        {
            if (_config.CreateAsDirectory)
            {
                if (!Directory.Exists(markerPath))
                {
                    Directory.CreateDirectory(markerPath);
                    stats.MarkerCreatedAt = DateTime.UtcNow;
                    stats.CreateCount++;
                    _logger.LogInformation("Created marker directory: {MarkerPath}", markerPath);
                }
            }
            else
            {
                if (!File.Exists(markerPath))
                {
                    // Create empty marker file
                    File.WriteAllBytes(markerPath, Array.Empty<byte>());
                    stats.MarkerCreatedAt = DateTime.UtcNow;
                    stats.CreateCount++;
                    _logger.LogInformation("Created marker file: {MarkerPath}", markerPath);
                }
            }

            stats.MarkerExists = true;
            stats.LastChecked = DateTime.UtcNow;
            stats.CheckCount++;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create marker at {MarkerPath}", markerPath);
            return false;
        }
    }

    /// <inheritdoc />
    public bool MarkerExists(string folderId, string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(folderPath);

        var markerPath = GetMarkerPath(folderId, folderPath);
        var stats = GetOrCreateStats(folderId);

        stats.LastChecked = DateTime.UtcNow;
        stats.CheckCount++;

        bool exists;
        if (_config.CreateAsDirectory)
        {
            exists = Directory.Exists(markerPath);
        }
        else
        {
            exists = File.Exists(markerPath);
        }

        stats.MarkerExists = exists;
        return exists;
    }

    /// <inheritdoc />
    public bool IsValidMarkerName(string markerName)
    {
        if (string.IsNullOrWhiteSpace(markerName))
        {
            return false;
        }

        if (markerName.Length > _config.MaxMarkerNameLength)
        {
            return false;
        }

        // Check for invalid characters
        if (markerName.IndexOfAny(_config.InvalidCharacters) >= 0)
        {
            return false;
        }

        // Check for reserved names (Windows)
        var reservedNames = new[] { "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(markerName).ToUpperInvariant();
        if (reservedNames.Contains(nameWithoutExtension))
        {
            return false;
        }

        // Check for path traversal
        if (markerName.Contains("..") || markerName.Contains('/') || markerName.Contains('\\'))
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetAllMarkerNames()
    {
        return _markerNames.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Get statistics for a folder.
    /// </summary>
    public MarkerStats GetStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var stats = GetOrCreateStats(folderId);
        stats.MarkerName = GetMarkerName(folderId);

        return new MarkerStats
        {
            FolderId = stats.FolderId,
            MarkerName = stats.MarkerName,
            MarkerExists = stats.MarkerExists,
            LastChecked = stats.LastChecked,
            MarkerCreatedAt = stats.MarkerCreatedAt,
            CheckCount = stats.CheckCount,
            CreateCount = stats.CreateCount
        };
    }

    private MarkerStats GetOrCreateStats(string folderId)
    {
        return _stats.GetOrAdd(folderId, id => new MarkerStats
        {
            FolderId = id,
            MarkerName = GetMarkerName(id)
        });
    }
}
