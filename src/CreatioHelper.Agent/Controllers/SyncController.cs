using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Contracts.Requests;
using CreatioHelper.Contracts.Responses;
using CreatioHelper.Agent.Authorization;
using CreatioHelper.Agent.Mapping;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Sync management API controller (based on Syncthing REST API)
/// Inspired by Syncthing's lib/api package
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.ReadRoles)]
public class SyncController : ControllerBase
{
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger<SyncController> _logger;

    public SyncController(ISyncEngine syncEngine, ILogger<SyncController> logger)
    {
        _syncEngine = syncEngine;
        _logger = logger;
    }

    /// <summary>
    /// Get device ID
    /// </summary>
    [HttpGet("device-id")]
    [Authorize(Roles = Roles.MonitorRoles)]
    public async Task<ActionResult<string>> GetDeviceId()
    {
        var config = await _syncEngine.GetConfigurationAsync();
        return Ok(config.DeviceId);
    }

    /// <summary>
    /// Get sync system status
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<SyncSystemStatus>> GetStatus()
    {
        var statistics = await _syncEngine.GetStatisticsAsync();
        var devices = await _syncEngine.GetDevicesAsync();
        var folders = await _syncEngine.GetFoldersAsync();

        return Ok(statistics.ToSystemStatus(devices));
    }

    /// <summary>
    /// Get all configured devices
    /// </summary>
    [HttpGet("devices")]
    public async Task<ActionResult<List<SyncDeviceDto>>> GetDevices()
    {
        var devices = await _syncEngine.GetDevicesAsync();
        var deviceDtos = devices.Select(d => d.ToDto()).ToList();

        return Ok(deviceDtos);
    }

    /// <summary>
    /// Add a new device
    /// </summary>
    [HttpPost("devices")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult<SyncDeviceDto>> AddDevice([FromBody] AddDeviceRequest request)
    {
        var device = await _syncEngine.AddDeviceAsync(
            request.DeviceId, 
            request.Name, 
            request.CertificateFingerprint, 
            request.Addresses);

        var deviceDto = device.ToDto();

        return CreatedAtAction(nameof(GetDevice), new { deviceId = device.DeviceId }, deviceDto);
    }

    /// <summary>
    /// Get a specific device
    /// </summary>
    [HttpGet("devices/{deviceId}")]
    public async Task<ActionResult<SyncDeviceDto>> GetDevice(string deviceId)
    {
        var devices = await _syncEngine.GetDevicesAsync();
        var device = devices.FirstOrDefault(d => d.DeviceId == deviceId);

        if (device == null)
            return NotFound();

        var deviceDto = device.ToDto();

        return Ok(deviceDto);
    }

    /// <summary>
    /// Pause a device
    /// </summary>
    [HttpPost("devices/{deviceId}/pause")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> PauseDevice(string deviceId)
    {
        try
        {
            await _syncEngine.PauseDeviceAsync(deviceId);
            return Ok();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing device {DeviceId}", deviceId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Resume a device
    /// </summary>
    [HttpPost("devices/{deviceId}/resume")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> ResumeDevice(string deviceId)
    {
        try
        {
            await _syncEngine.ResumeDeviceAsync(deviceId);
            return Ok();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming device {DeviceId}", deviceId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get all configured folders
    /// </summary>
    [HttpGet("folders")]
    public async Task<ActionResult<List<SyncFolderDto>>> GetFolders()
    {
        var folders = await _syncEngine.GetFoldersAsync();
        var folderDtos = new List<SyncFolderDto>();

        foreach (var folder in folders)
        {
            var status = await _syncEngine.GetSyncStatusAsync(folder.Id);
            folderDtos.Add(folder.ToDto(status));
        }

        return Ok(folderDtos);
    }

    /// <summary>
    /// Add a new folder
    /// </summary>
    [HttpPost("folders")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult<SyncFolderDto>> AddFolder([FromBody] AddFolderRequest request)
    {
        var folder = await _syncEngine.AddFolderAsync(
            request.FolderId, 
            request.Label, 
            request.Path, 
            request.Type);

        var status = await _syncEngine.GetSyncStatusAsync(folder.Id);
        var folderDto = folder.ToDto(status);

        return CreatedAtAction(nameof(GetFolder), new { folderId = folder.Id }, folderDto);
    }

    /// <summary>
    /// Get a specific folder
    /// </summary>
    [HttpGet("folders/{folderId}")]
    public async Task<ActionResult<SyncFolderDto>> GetFolder(string folderId)
    {
        var folders = await _syncEngine.GetFoldersAsync();
        var folder = folders.FirstOrDefault(f => f.FolderId == folderId);

        if (folder == null)
            return NotFound();

        var status = await _syncEngine.GetSyncStatusAsync(folder.Id);
        var folderDto = folder.ToDto(status);

        return Ok(folderDto);
    }

    /// <summary>
    /// Pause a folder
    /// </summary>
    [HttpPost("folders/{folderId}/pause")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> PauseFolder(string folderId)
    {
        try
        {
            await _syncEngine.PauseFolderAsync(folderId);
            return Ok();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing folder {FolderId}", folderId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Resume a folder
    /// </summary>
    [HttpPost("folders/{folderId}/resume")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> ResumeFolder(string folderId)
    {
        try
        {
            await _syncEngine.ResumeFolderAsync(folderId);
            return Ok();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming folder {FolderId}", folderId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Scan a folder
    /// </summary>
    [HttpPost("folders/{folderId}/scan")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> ScanFolder(string folderId, [FromQuery] bool deep = false)
    {
        try
        {
            var folder = await _syncEngine.GetFolderAsync(folderId);
            if (folder == null)
            {
                return NotFound();
            }

            _syncEngine.QueueScan(folderId, deep);
            return Ok();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {FolderId}", folderId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Share a folder with a device
    /// </summary>
    [HttpPost("folders/{folderId}/share")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> ShareFolder(string folderId, [FromBody] ShareFolderRequest request)
    {
        try
        {
            await _syncEngine.ShareFolderWithDeviceAsync(folderId, request.DeviceId);
            return Ok();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument sharing folder {FolderId} with device {DeviceId}", folderId, request.DeviceId);
            return BadRequest(new { error = "Invalid folder or device" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sharing folder {FolderId} with device {DeviceId}", folderId, request.DeviceId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Unshare a folder from a device
    /// </summary>
    [HttpPost("folders/{folderId}/unshare")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> UnshareFolder(string folderId, [FromBody] ShareFolderRequest request)
    {
        try
        {
            await _syncEngine.UnshareFolderFromDeviceAsync(folderId, request.DeviceId);
            return Ok();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument unsharing folder {FolderId} from device {DeviceId}", folderId, request.DeviceId);
            return BadRequest(new { error = "Invalid folder or device" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsharing folder {FolderId} from device {DeviceId}", folderId, request.DeviceId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get system statistics
    /// </summary>
    [HttpGet("statistics")]
    public async Task<ActionResult<SyncStatistics>> GetStatistics()
    {
        var statistics = await _syncEngine.GetStatisticsAsync();
        return Ok(statistics);
    }
}