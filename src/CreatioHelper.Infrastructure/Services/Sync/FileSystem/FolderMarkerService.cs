using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Manages .stfolder markers for root folders (based on Syncthing folder.go)
/// Folder markers are used to detect if a folder is still mounted/accessible
/// </summary>
public interface IFolderMarkerService
{
    /// <summary>
    /// Default marker name
    /// </summary>
    string DefaultMarkerName { get; }

    /// <summary>
    /// Create a folder marker if it doesn't exist
    /// </summary>
    Task<bool> EnsureMarkerExistsAsync(string folderPath, string? markerName = null, CancellationToken ct = default);

    /// <summary>
    /// Check if a folder marker exists
    /// </summary>
    Task<bool> MarkerExistsAsync(string folderPath, string? markerName = null, CancellationToken ct = default);

    /// <summary>
    /// Check if a folder is a valid sync folder (has marker)
    /// </summary>
    Task<bool> IsSyncFolderAsync(string folderPath, string? markerName = null, CancellationToken ct = default);

    /// <summary>
    /// Remove a folder marker
    /// </summary>
    Task<bool> RemoveMarkerAsync(string folderPath, string? markerName = null, CancellationToken ct = default);

    /// <summary>
    /// Get marker information
    /// </summary>
    Task<FolderMarkerInfo?> GetMarkerInfoAsync(string folderPath, string? markerName = null, CancellationToken ct = default);
}

/// <summary>
/// Information about a folder marker
/// </summary>
public record FolderMarkerInfo
{
    public string FolderPath { get; init; } = string.Empty;
    public string MarkerPath { get; init; } = string.Empty;
    public string MarkerName { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool Exists { get; init; }
}

/// <summary>
/// Implementation of folder marker management (based on Syncthing folder.go)
/// </summary>
public class FolderMarkerService : IFolderMarkerService
{
    private readonly ILogger<FolderMarkerService> _logger;

    public string DefaultMarkerName => ".stfolder";

    public FolderMarkerService(ILogger<FolderMarkerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Create a folder marker if it doesn't exist
    /// Creates either a directory or file depending on the marker name
    /// </summary>
    public async Task<bool> EnsureMarkerExistsAsync(string folderPath, string? markerName = null, CancellationToken ct = default)
    {
        var marker = markerName ?? DefaultMarkerName;

        try
        {
            if (!Directory.Exists(folderPath))
            {
                _logger.LogWarning("Cannot create marker: folder does not exist: {FolderPath}", folderPath);
                return false;
            }

            var markerPath = Path.Combine(folderPath, marker);

            // Check if marker already exists
            if (Directory.Exists(markerPath) || File.Exists(markerPath))
            {
                _logger.LogTrace("Marker already exists: {MarkerPath}", markerPath);
                return true;
            }

            await Task.Run(() =>
            {
                // Create marker based on whether it looks like a directory marker
                // Check if there's no '.' after position 3 (e.g., ".stfolder" has no extension)
                if (marker.StartsWith(".st") && marker.IndexOf('.', 3) < 0)
                {
                    // Default Syncthing behavior: create as directory
                    Directory.CreateDirectory(markerPath);
                    _logger.LogDebug("Created directory marker: {MarkerPath}", markerPath);
                }
                else
                {
                    // Custom marker: create as file
                    using var fs = File.Create(markerPath);
                    _logger.LogDebug("Created file marker: {MarkerPath}", markerPath);
                }

                // Set hidden attribute on Windows
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(markerPath, File.GetAttributes(markerPath) | FileAttributes.Hidden);
                }
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create folder marker: {FolderPath}/{Marker}", folderPath, marker);
            return false;
        }
    }

    /// <summary>
    /// Check if a folder marker exists
    /// </summary>
    public Task<bool> MarkerExistsAsync(string folderPath, string? markerName = null, CancellationToken ct = default)
    {
        var marker = markerName ?? DefaultMarkerName;
        var markerPath = Path.Combine(folderPath, marker);

        return Task.FromResult(Directory.Exists(markerPath) || File.Exists(markerPath));
    }

    /// <summary>
    /// Check if a folder is a valid sync folder (has marker and is accessible)
    /// </summary>
    public async Task<bool> IsSyncFolderAsync(string folderPath, string? markerName = null, CancellationToken ct = default)
    {
        try
        {
            // Check if folder exists
            if (!Directory.Exists(folderPath))
            {
                _logger.LogDebug("Folder does not exist: {FolderPath}", folderPath);
                return false;
            }

            // Check if marker exists
            if (!await MarkerExistsAsync(folderPath, markerName, ct))
            {
                _logger.LogDebug("Folder marker missing: {FolderPath}", folderPath);
                return false;
            }

            // Try to access the folder to verify it's mounted/accessible
            try
            {
                await Task.Run(() =>
                {
                    _ = Directory.GetFiles(folderPath, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        MaxRecursionDepth = 0
                    }).Take(1).ToList();
                }, ct);

                return true;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Folder not accessible: {FolderPath}", folderPath);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if folder is sync folder: {FolderPath}", folderPath);
            return false;
        }
    }

    /// <summary>
    /// Remove a folder marker
    /// </summary>
    public async Task<bool> RemoveMarkerAsync(string folderPath, string? markerName = null, CancellationToken ct = default)
    {
        var marker = markerName ?? DefaultMarkerName;
        var markerPath = Path.Combine(folderPath, marker);

        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(markerPath))
                {
                    Directory.Delete(markerPath, recursive: false);
                    _logger.LogDebug("Removed directory marker: {MarkerPath}", markerPath);
                }
                else if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                    _logger.LogDebug("Removed file marker: {MarkerPath}", markerPath);
                }
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove folder marker: {MarkerPath}", markerPath);
            return false;
        }
    }

    /// <summary>
    /// Get marker information
    /// </summary>
    public async Task<FolderMarkerInfo?> GetMarkerInfoAsync(string folderPath, string? markerName = null, CancellationToken ct = default)
    {
        var marker = markerName ?? DefaultMarkerName;
        var markerPath = Path.Combine(folderPath, marker);

        try
        {
            return await Task.Run(() =>
            {
                if (Directory.Exists(markerPath))
                {
                    var dirInfo = new DirectoryInfo(markerPath);
                    return new FolderMarkerInfo
                    {
                        FolderPath = folderPath,
                        MarkerPath = markerPath,
                        MarkerName = marker,
                        IsDirectory = true,
                        CreatedAt = dirInfo.CreationTimeUtc,
                        Exists = true
                    };
                }

                if (File.Exists(markerPath))
                {
                    var fileInfo = new FileInfo(markerPath);
                    return new FolderMarkerInfo
                    {
                        FolderPath = folderPath,
                        MarkerPath = markerPath,
                        MarkerName = marker,
                        IsDirectory = false,
                        CreatedAt = fileInfo.CreationTimeUtc,
                        Exists = true
                    };
                }

                return new FolderMarkerInfo
                {
                    FolderPath = folderPath,
                    MarkerPath = markerPath,
                    MarkerName = marker,
                    IsDirectory = false,
                    CreatedAt = DateTime.MinValue,
                    Exists = false
                };
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting marker info: {MarkerPath}", markerPath);
            return null;
        }
    }
}
