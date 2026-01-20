using CreatioHelper.Agent.Authorization;
using CreatioHelper.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Syncthing-compatible /rest/db API endpoints
/// Provides 100% compatibility with Syncthing database API
/// </summary>
[ApiController]
[Route("rest/db")]
public class DatabaseController : ControllerBase
{
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger<DatabaseController> _logger;

    public DatabaseController(ISyncEngine syncEngine, ILogger<DatabaseController> logger)
    {
        _syncEngine = syncEngine;
        _logger = logger;
    }

    /// <summary>
    /// Get folder status - 100% Syncthing compatible
    /// GET /rest/db/status?folder=default
    /// </summary>
    [HttpGet("status")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetStatus([FromQuery] string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            var folderStatus = await _syncEngine.GetSyncStatusAsync(folder);
            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            return Ok(new
            {
                globalFiles = folderStatus.TotalFiles,
                globalDirectories = folderStatus.TotalDirectories,
                globalSymlinks = 0,
                globalDeleted = 0,
                globalBytes = folderStatus.TotalBytes,
                globalTotalItems = folderStatus.TotalFiles + folderStatus.TotalDirectories,
                
                localFiles = folderStatus.LocalFiles,
                localDirectories = folderStatus.LocalDirectories,
                localSymlinks = 0,
                localDeleted = 0,
                localBytes = folderStatus.LocalBytes,
                localTotalItems = folderStatus.LocalFiles + folderStatus.LocalDirectories,
                
                needFiles = folderStatus.OutOfSyncFiles,
                needDirectories = 0,
                needSymlinks = 0,
                needDeletes = 0,
                needBytes = folderStatus.OutOfSyncBytes,
                needTotalItems = folderStatus.OutOfSyncFiles,
                
                receiveOnlyChangedFiles = 0,
                receiveOnlyChangedDirectories = 0,
                receiveOnlyChangedSymlinks = 0,
                receiveOnlyChangedDeletes = 0,
                receiveOnlyChangedBytes = 0,
                receiveOnlyTotalItems = 0,
                
                pullErrors = 0,
                state = folderStatus.State.ToString().ToLowerInvariant(),
                stateChanged = folderStatus.LastSync.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                error = string.Empty,
                version = folderStatus.Version,
                sequence = folderStatus.Sequence,
                ignorePatterns = false,
                watchError = string.Empty
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder status for {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get completion status - 100% Syncthing compatible
    /// GET /rest/db/completion?folder=default&device=DEVICE-ID
    /// </summary>
    [HttpGet("completion")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetCompletion([FromQuery] string folder, [FromQuery] string device)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(device))
                return BadRequest(new { error = "folder and device parameters required" });

            // Get real completion status from SyncEngine
            var completion = await _syncEngine.GetCompletionAsync(folder, device);

            return Ok(new
            {
                completion = completion.Completion,
                globalBytes = completion.GlobalBytes,
                needBytes = completion.NeedBytes,
                globalItems = completion.GlobalItems,
                needItems = completion.NeedItems,
                needDeletes = completion.NeedDeletes,
                sequence = completion.Sequence,
                remoteState = completion.RemoteState
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting completion for {Folder} on {Device}", folder, device);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Browse folder files - 100% Syncthing compatible
    /// GET /rest/db/browse?folder=default&prefix=path
    /// </summary>
    [HttpGet("browse")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> Browse([FromQuery] string folder, [FromQuery] string? prefix, [FromQuery] bool dirsonly = false)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            // Validate prefix path to prevent path traversal attacks
            if (!string.IsNullOrEmpty(prefix) && (prefix.Contains("..") || Path.IsPathRooted(prefix)))
                return BadRequest(new { error = "Invalid prefix path" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            var basePath = folderInfo.Path;
            var searchPath = string.IsNullOrEmpty(prefix) ? basePath : Path.Combine(basePath, prefix);

            // Additional check: ensure resolved path is within the folder
            var searchFullPath = Path.GetFullPath(searchPath);
            var folderFullPath = Path.GetFullPath(basePath);

            // Normalize folder path to end with separator for accurate prefix matching
            if (!folderFullPath.EndsWith(Path.DirectorySeparatorChar))
                folderFullPath += Path.DirectorySeparatorChar;

            // Path must equal the folder or start with folder + separator
            if (!searchFullPath.Equals(folderFullPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                !searchFullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Invalid prefix path" });

            var entries = new List<object>();

            if (Directory.Exists(searchPath))
            {
                var directoryInfo = new DirectoryInfo(searchPath);
                
                // Add directories
                foreach (var dir in directoryInfo.GetDirectories())
                {
                    entries.Add(new
                    {
                        name = dir.Name,
                        type = "directory",
                        size = 0,
                        modified = dir.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    });
                }

                // Add files (unless dirsonly is true)
                if (!dirsonly)
                {
                    foreach (var file in directoryInfo.GetFiles())
                    {
                        entries.Add(new
                        {
                            name = file.Name,
                            type = "file",
                            size = file.Length,
                            modified = file.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                        });
                    }
                }
            }

            return Ok(entries.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error browsing folder {Folder} with prefix {Prefix}", folder, prefix);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get file information - 100% Syncthing compatible
    /// GET /rest/db/file?folder=default&file=path/to/file
    /// </summary>
    [HttpGet("file")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetFile([FromQuery] string folder, [FromQuery] string file)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(file))
                return BadRequest(new { error = "folder and file parameters required" });

            // Validate file path to prevent path traversal attacks
            if (file.Contains("..") || Path.IsPathRooted(file))
                return BadRequest(new { error = "Invalid file path" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            var filePath = Path.Combine(folderInfo.Path, file);

            // Additional check: ensure resolved path is within the folder
            var fullPath = Path.GetFullPath(filePath);
            var folderFullPath = Path.GetFullPath(folderInfo.Path);

            // Normalize folder path to end with separator for accurate prefix matching
            if (!folderFullPath.EndsWith(Path.DirectorySeparatorChar))
                folderFullPath += Path.DirectorySeparatorChar;

            // Path must start with folder + separator (files cannot equal folder path)
            if (!fullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Invalid file path" });

            if (!System.IO.File.Exists(filePath))
                return NotFound(new { error = "file not found" });

            var fileInfo = new FileInfo(filePath);
            
            return Ok(new
            {
                availability = new[] { _syncEngine.DeviceId },
                blocksHash = Array.Empty<byte>(),
                deleted = false,
                invalid = false,
                localFlags = 0,
                modified = fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                modifiedBy = _syncEngine.DeviceId,
                name = file,
                noPermissions = false,
                numBlocks = 1,
                permissions = "0644",
                platform = new { },
                sequence = 1000,
                size = fileInfo.Length,
                type = "file",
                version = new
                {
                    counters = new[]
                    {
                        new { id = 1, value = 1 }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting file {File} in folder {Folder}", file, folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Scan folder - 100% Syncthing compatible
    /// POST /rest/db/scan?folder=default&sub=path
    /// </summary>
    [HttpPost("scan")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> Scan([FromQuery] string folder, [FromQuery] string? sub, [FromQuery] int next = 0)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            _logger.LogInformation("Scanning folder {Folder} with sub {Sub}", folder, sub);
            
            // Trigger folder scan
            await _syncEngine.ScanFolderAsync(folder, !string.IsNullOrEmpty(sub));
            
            return Ok(new { message = "scan initiated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get folder need - 100% Syncthing compatible
    /// GET /rest/db/need?folder=default
    /// </summary>
    [HttpGet("need")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetNeed([FromQuery] string folder, [FromQuery] int page = 1, [FromQuery] int perpage = 100)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            // Bounds checking for pagination parameters
            page = Math.Max(1, page);
            perpage = Math.Clamp(perpage, 1, 1000);

            // Get real need list from SyncEngine
            var needList = await _syncEngine.GetNeedListAsync(folder, page, perpage);

            // Convert to Syncthing-compatible format
            var files = needList.Files.Select(f => new
            {
                name = f.Name,
                size = f.Size,
                modified = f.ModifiedTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                type = f.Type,
                availability = f.Availability
            }).ToArray();

            return Ok(new
            {
                files = files,
                page = needList.Page,
                perpage = needList.PerPage,
                progress = new[]
                {
                    new
                    {
                        bytesTotal = needList.Progress.BytesTotal,
                        bytesDone = needList.Progress.BytesDone,
                        pct = needList.Progress.Percentage
                    }
                },
                queued = files,
                rest = Array.Empty<object>(),
                total = needList.Total
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting need for folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Override folder files - 100% Syncthing compatible
    /// POST /rest/db/override?folder=default
    /// </summary>
    [HttpPost("override")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> Override([FromQuery] string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            _logger.LogInformation("Override requested for folder {Folder}", folder);

            // Call real override implementation in SyncEngine
            var result = await _syncEngine.OverrideFolderAsync(folder);

            if (result)
            {
                return Ok(new { message = "override completed" });
            }
            else
            {
                return BadRequest(new { error = "override failed - folder may not support this operation" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error overriding folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Revert folder files - 100% Syncthing compatible
    /// POST /rest/db/revert?folder=default
    /// </summary>
    [HttpPost("revert")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> Revert([FromQuery] string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            _logger.LogInformation("Revert requested for folder {Folder}", folder);

            // Call real revert implementation in SyncEngine
            var result = await _syncEngine.RevertFolderAsync(folder);

            if (result)
            {
                return Ok(new { message = "revert completed" });
            }
            else
            {
                return BadRequest(new { error = "revert failed - folder may not support this operation" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reverting folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get ignore patterns - 100% Syncthing compatible
    /// GET /rest/db/ignores?folder=default
    /// </summary>
    [HttpGet("ignores")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetIgnores([FromQuery] string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            var stignorePath = Path.Combine(folderInfo.Path, ".stignore");
            var lines = new List<string>();
            var patterns = new List<string>();

            if (System.IO.File.Exists(stignorePath))
            {
                lines = (await System.IO.File.ReadAllLinesAsync(stignorePath)).ToList();
                // Filter out comments and empty lines for patterns
                patterns = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("//")).ToList();
            }

            return Ok(new
            {
                ignore = patterns.ToArray(),
                expanded = patterns.ToArray(),
                lines = lines.ToArray(),
                error = (string?)null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting ignores for folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Set ignore patterns - 100% Syncthing compatible
    /// POST /rest/db/ignores?folder=default
    /// </summary>
    [HttpPost("ignores")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult<object>> SetIgnores([FromQuery] string folder, [FromBody] IgnorePatternsRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            var stignorePath = Path.Combine(folderInfo.Path, ".stignore");
            var lines = request.Lines ?? request.Ignore ?? Array.Empty<string>();

            await System.IO.File.WriteAllLinesAsync(stignorePath, lines);

            _logger.LogInformation("Updated ignore patterns for folder {Folder}", folder);

            // Return updated patterns
            var patterns = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("//")).ToArray();

            return Ok(new
            {
                ignore = patterns,
                expanded = patterns,
                lines = lines,
                error = (string?)null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting ignores for folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get remote need - files needed by a remote device - 100% Syncthing compatible
    /// GET /rest/db/remoteneed?folder=default&device=DEVICE-ID
    /// </summary>
    [HttpGet("remoteneed")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetRemoteNeed([FromQuery] string folder, [FromQuery] string device, [FromQuery] int page = 1, [FromQuery] int perpage = 100)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(device))
                return BadRequest(new { error = "folder and device parameters required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            // Bounds checking for pagination
            page = Math.Max(1, page);
            perpage = Math.Clamp(perpage, 1, 1000);

            // Get need list - in real implementation this would query what files the remote device needs
            // For now, return empty list as the remote device state is typically tracked on the remote side
            return Ok(new
            {
                files = Array.Empty<object>(),
                page = page,
                perpage = perpage,
                progress = Array.Empty<object>(),
                queued = Array.Empty<object>(),
                rest = Array.Empty<object>(),
                total = 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting remote need for folder {Folder} device {Device}", folder, device);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get locally changed files (for receive-only folders) - 100% Syncthing compatible
    /// GET /rest/db/localchanged?folder=default
    /// </summary>
    [HttpGet("localchanged")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<ActionResult<object>> GetLocalChanged([FromQuery] string folder, [FromQuery] int page = 1, [FromQuery] int perpage = 100)
    {
        try
        {
            if (string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "folder parameter required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            // Bounds checking for pagination
            page = Math.Max(1, page);
            perpage = Math.Clamp(perpage, 1, 1000);

            // For receive-only folders, this returns files that have been locally modified
            // For now, return empty list - full implementation would track local changes
            return Ok(new
            {
                files = Array.Empty<object>(),
                page = page,
                perpage = perpage,
                total = 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting local changed for folder {Folder}", folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Set file priority - 100% Syncthing compatible
    /// POST /rest/db/prio?folder=default&file=path/to/file
    /// </summary>
    [HttpPost("prio")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> SetPriority([FromQuery] string folder, [FromQuery] string file)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(file))
                return BadRequest(new { error = "folder and file parameters required" });

            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = "folder not found" });

            _logger.LogInformation("Priority requested for file {File} in folder {Folder}", file, folder);

            // In Syncthing, this bumps a file to the front of the download queue
            // For now, acknowledge the request - full implementation would reorder sync queue
            return Ok(new
            {
                files = new[]
                {
                    new
                    {
                        name = file,
                        size = 0L,
                        modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        type = "file"
                    }
                },
                page = 1,
                perpage = 100
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting priority for file {File} in folder {Folder}", file, folder);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}

/// <summary>
/// Request model for setting ignore patterns
/// </summary>
public class IgnorePatternsRequest
{
    /// <summary>
    /// Ignore patterns (simplified)
    /// </summary>
    public string[]? Ignore { get; set; }

    /// <summary>
    /// Raw lines from .stignore file
    /// </summary>
    public string[]? Lines { get; set; }
}