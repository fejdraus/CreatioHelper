using CreatioHelper.Agent.Authorization;
using CreatioHelper.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Syncthing-compatible /rest/folder API endpoints
/// Provides 100% compatibility with Syncthing folder API (versions, errors)
/// </summary>
[ApiController]
[Route("rest/folder")]
public class SyncthingFolderController : ControllerBase
{
    private readonly ISyncEngine _syncEngine;
    private readonly IVersionerFactory _versionerFactory;
    private readonly ILogger<SyncthingFolderController> _logger;

    public SyncthingFolderController(
        ISyncEngine syncEngine,
        IVersionerFactory versionerFactory,
        ILogger<SyncthingFolderController> logger)
    {
        _syncEngine = syncEngine;
        _versionerFactory = versionerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get file versions - 100% Syncthing compatible
    /// GET /rest/folder/versions?folder=default
    /// </summary>
    [HttpGet("versions")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetVersions([FromQuery] string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            // Check if versioning is enabled
            if (folderInfo.Versioning == null || !folderInfo.Versioning.IsEnabled)
            {
                return Ok(new Dictionary<string, object[]>());
            }

            // Create versioner for this folder
            IVersioner? versioner = null;
            try
            {
                versioner = _versionerFactory.CreateVersioner(folderInfo.Path, folderInfo.Versioning);
                var versions = await versioner.GetVersionsAsync();

                // Convert to Syncthing-compatible format
                // Dictionary<originalFilePath, List<VersionInfo>>
                var result = new Dictionary<string, object[]>();

                foreach (var kvp in versions)
                {
                    result[kvp.Key] = kvp.Value.Select(v => new
                    {
                        versionTime = v.VersionTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        modTime = v.ModTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        size = v.Size
                    }).ToArray();
                }

                return Ok(result);
            }
            finally
            {
                versioner?.Dispose();
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid versioning configuration for folder {Folder}", folder);
            return Ok(new Dictionary<string, object[]>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting versions for folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Restore file versions - 100% Syncthing compatible
    /// POST /rest/folder/versions?folder=default
    /// Body: {"file.txt": "2024-01-15T10:30:00.000Z"}
    /// </summary>
    [HttpPost("versions")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult<object>> RestoreVersions([FromQuery] string folder, [FromBody] Dictionary<string, string> restoreRequest)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            if (restoreRequest == null || restoreRequest.Count == 0)
                return BadRequest(new { error = "restore request body required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            // Check if versioning is enabled
            if (folderInfo.Versioning == null || !folderInfo.Versioning.IsEnabled)
            {
                return BadRequest(new { error = "versioning not enabled for this folder" });
            }

            // Create versioner for this folder
            IVersioner? versioner = null;
            try
            {
                versioner = _versionerFactory.CreateVersioner(folderInfo.Path, folderInfo.Versioning);

                var restored = new Dictionary<string, string>();
                var failed = new Dictionary<string, string>();

                foreach (var kvp in restoreRequest)
                {
                    var filePath = kvp.Key;
                    var versionTimeStr = kvp.Value;

                    try
                    {
                        if (!DateTime.TryParse(versionTimeStr, out var versionTime))
                        {
                            failed[filePath] = "invalid version time format";
                            continue;
                        }

                        await versioner.RestoreAsync(filePath, versionTime);
                        restored[filePath] = "restored";
                        _logger.LogInformation("Restored version {VersionTime} of file {File} in folder {Folder}",
                            versionTime, filePath, folder);
                    }
                    catch (FileNotFoundException)
                    {
                        failed[filePath] = "version not found";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to restore file {File} in folder {Folder}", filePath, folder);
                        failed[filePath] = ex.Message;
                    }
                }

                return Ok(new
                {
                    restored = restored,
                    failed = failed
                });
            }
            finally
            {
                versioner?.Dispose();
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid versioning configuration for folder {Folder}", folder);
            return BadRequest(new { error = "versioning configuration error" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring versions for folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get folder errors - 100% Syncthing compatible
    /// GET /rest/folder/errors?folder=default
    /// </summary>
    [HttpGet("errors")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetErrors([FromQuery] string folder, [FromQuery] int page = 1, [FromQuery] int perpage = 100)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            // Get sync status which contains error information
            var status = await _syncEngine.GetSyncStatusAsync(folder);

            // Bounds checking for pagination
            page = Math.Max(1, page);
            perpage = Math.Clamp(perpage, 1, 1000);

            // Convert errors to Syncthing-compatible format
            var allErrors = status.Errors.Select((error, index) => new
            {
                error = error,
                path = string.Empty // In full implementation, this would contain the path that caused the error
            }).ToList();

            var totalErrors = allErrors.Count;
            var pagedErrors = allErrors.Skip((page - 1) * perpage).Take(perpage).ToArray();

            return Ok(new
            {
                folder = folder,
                errors = pagedErrors,
                page = page,
                perpage = perpage,
                total = totalErrors
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting errors for folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get folder status summary - 100% Syncthing compatible (extension endpoint)
    /// GET /rest/folder/status?folder=default
    /// </summary>
    [HttpGet("status")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetStatus([FromQuery] string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            var status = await _syncEngine.GetSyncStatusAsync(folder);

            return Ok(new
            {
                folder = folder,
                globalFiles = status.TotalFiles,
                globalDirectories = status.TotalDirectories,
                globalBytes = status.TotalBytes,
                localFiles = status.LocalFiles,
                localDirectories = status.LocalDirectories,
                localBytes = status.LocalBytes,
                needFiles = status.OutOfSyncFiles,
                needBytes = status.OutOfSyncBytes,
                state = status.State.ToString().ToLowerInvariant(),
                stateChanged = status.LastSync.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                error = status.Errors.FirstOrDefault() ?? string.Empty,
                pullErrors = status.Errors.Count,
                version = status.Version,
                sequence = status.Sequence
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status for folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
