using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Contracts.Requests;
using CreatioHelper.Contracts.Responses;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Sync management API controller (based on Syncthing REST API)
/// Inspired by Syncthing's lib/api package
/// </summary>
[ApiController]
[Route("api/[controller]")]
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
    public async Task<ActionResult<string>> GetDeviceId()
    {
        try
        {
            var config = await _syncEngine.GetConfigurationAsync();
            return Ok(config.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device ID");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get sync system status
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<SyncSystemStatus>> GetStatus()
    {
        try
        {
            var statistics = await _syncEngine.GetStatisticsAsync();
            var devices = await _syncEngine.GetDevicesAsync();
            var folders = await _syncEngine.GetFoldersAsync();

            var status = new SyncSystemStatus
            {
                Uptime = statistics.Uptime,
                ConnectedDevices = statistics.ConnectedDevices,
                TotalDevices = statistics.TotalDevices,
                SyncedFolders = statistics.SyncedFolders,
                TotalFolders = statistics.TotalFolders,
                TotalBytesIn = statistics.TotalBytesIn,
                TotalBytesOut = statistics.TotalBytesOut,
                IsOnline = devices.Any(d => d.IsConnected)
            };

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sync status");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get all configured devices
    /// </summary>
    [HttpGet("devices")]
    public async Task<ActionResult<List<SyncDeviceDto>>> GetDevices()
    {
        try
        {
            var devices = await _syncEngine.GetDevicesAsync();
            var deviceDtos = devices.Select(d => new SyncDeviceDto
            {
                DeviceId = d.DeviceId,
                Name = d.DeviceName,
                IsConnected = d.IsConnected,
                LastSeen = d.LastSeen ?? DateTime.MinValue,
                Status = d.Status.ToString(),
                IsPaused = d.IsPaused,
                Addresses = d.Addresses
            }).ToList();

            return Ok(deviceDtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting devices");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Add a new device
    /// </summary>
    [HttpPost("devices")]
    public async Task<ActionResult<SyncDeviceDto>> AddDevice([FromBody] AddDeviceRequest request)
    {
        try
        {
            var device = await _syncEngine.AddDeviceAsync(
                request.DeviceId, 
                request.Name, 
                request.CertificateFingerprint, 
                request.Addresses);

            var deviceDto = new SyncDeviceDto
            {
                DeviceId = device.DeviceId,
                Name = device.DeviceName,
                IsConnected = device.IsConnected,
                LastSeen = device.LastSeen ?? DateTime.MinValue,
                Status = device.Status.ToString(),
                IsPaused = device.IsPaused,
                Addresses = device.Addresses
            };

            return CreatedAtAction(nameof(GetDevice), new { deviceId = device.DeviceId }, deviceDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding device {DeviceId}", request.DeviceId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific device
    /// </summary>
    [HttpGet("devices/{deviceId}")]
    public async Task<ActionResult<SyncDeviceDto>> GetDevice(string deviceId)
    {
        try
        {
            var devices = await _syncEngine.GetDevicesAsync();
            var device = devices.FirstOrDefault(d => d.DeviceId == deviceId);

            if (device == null)
                return NotFound();

            var deviceDto = new SyncDeviceDto
            {
                DeviceId = device.DeviceId,
                Name = device.DeviceName,
                IsConnected = device.IsConnected,
                LastSeen = device.LastSeen ?? DateTime.MinValue,
                Status = device.Status.ToString(),
                IsPaused = device.IsPaused,
                Addresses = device.Addresses
            };

            return Ok(deviceDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device {DeviceId}", deviceId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Pause a device
    /// </summary>
    [HttpPost("devices/{deviceId}/pause")]
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
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Resume a device
    /// </summary>
    [HttpPost("devices/{deviceId}/resume")]
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
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get all configured folders
    /// </summary>
    [HttpGet("folders")]
    public async Task<ActionResult<List<SyncFolderDto>>> GetFolders()
    {
        try
        {
            var folders = await _syncEngine.GetFoldersAsync();
            var folderDtos = new List<SyncFolderDto>();

            foreach (var folder in folders)
            {
                var status = await _syncEngine.GetSyncStatusAsync(folder.Id);
                folderDtos.Add(new SyncFolderDto
                {
                    FolderId = folder.Id,
                    Label = folder.Label,
                    Path = folder.Path,
                    Type = folder.Type.ToString(),
                    IsPaused = folder.IsPaused,
                    State = status.State.ToString(),
                    GlobalBytes = status.GlobalBytes,
                    LocalBytes = status.LocalBytes,
                    GlobalFiles = status.GlobalFiles,
                    LocalFiles = status.LocalFiles,
                    LastScan = status.LastScan,
                    LastSync = status.LastSync,
                    DeviceIds = folder.Devices.ToList()
                });
            }

            return Ok(folderDtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folders");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Add a new folder
    /// </summary>
    [HttpPost("folders")]
    public async Task<ActionResult<SyncFolderDto>> AddFolder([FromBody] AddFolderRequest request)
    {
        try
        {
            var folder = await _syncEngine.AddFolderAsync(
                request.FolderId, 
                request.Label, 
                request.Path, 
                request.Type);

            var status = await _syncEngine.GetSyncStatusAsync(folder.Id);
            var folderDto = new SyncFolderDto
            {
                FolderId = folder.Id,
                Label = folder.Label,
                Path = folder.Path,
                Type = folder.Type.ToString(),
                IsPaused = folder.IsPaused,
                State = status.State.ToString(),
                GlobalBytes = status.GlobalBytes,
                LocalBytes = status.LocalBytes,
                GlobalFiles = status.GlobalFiles,
                LocalFiles = status.LocalFiles,
                LastScan = status.LastScan,
                LastSync = status.LastSync,
                DeviceIds = folder.Devices.ToList()
            };

            return CreatedAtAction(nameof(GetFolder), new { folderId = folder.Id }, folderDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding folder {FolderId}", request.FolderId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific folder
    /// </summary>
    [HttpGet("folders/{folderId}")]
    public async Task<ActionResult<SyncFolderDto>> GetFolder(string folderId)
    {
        try
        {
            var folders = await _syncEngine.GetFoldersAsync();
            var folder = folders.FirstOrDefault(f => f.FolderId == folderId);

            if (folder == null)
                return NotFound();

            var status = await _syncEngine.GetSyncStatusAsync(folder.Id);
            var folderDto = new SyncFolderDto
            {
                FolderId = folder.Id,
                Label = folder.Label,
                Path = folder.Path,
                Type = folder.Type.ToString(),
                IsPaused = folder.IsPaused,
                State = status.State.ToString(),
                GlobalBytes = status.GlobalBytes,
                LocalBytes = status.LocalBytes,
                GlobalFiles = status.GlobalFiles,
                LocalFiles = status.LocalFiles,
                LastScan = status.LastScan,
                LastSync = status.LastSync,
                DeviceIds = folder.Devices.ToList()
            };

            return Ok(folderDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder {FolderId}", folderId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Pause a folder
    /// </summary>
    [HttpPost("folders/{folderId}/pause")]
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
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Resume a folder
    /// </summary>
    [HttpPost("folders/{folderId}/resume")]
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
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Scan a folder
    /// </summary>
    [HttpPost("folders/{folderId}/scan")]
    public async Task<ActionResult> ScanFolder(string folderId, [FromQuery] bool deep = false)
    {
        try
        {
            await _syncEngine.ScanFolderAsync(folderId, deep);
            return Ok();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {FolderId}", folderId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Share a folder with a device
    /// </summary>
    [HttpPost("folders/{folderId}/share")]
    public async Task<ActionResult> ShareFolder(string folderId, [FromBody] ShareFolderRequest request)
    {
        try
        {
            await _syncEngine.ShareFolderWithDeviceAsync(folderId, request.DeviceId);
            return Ok();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sharing folder {FolderId} with device {DeviceId}", folderId, request.DeviceId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Unshare a folder from a device
    /// </summary>
    [HttpPost("folders/{folderId}/unshare")]
    public async Task<ActionResult> UnshareFolder(string folderId, [FromBody] ShareFolderRequest request)
    {
        try
        {
            await _syncEngine.UnshareFolderFromDeviceAsync(folderId, request.DeviceId);
            return Ok();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsharing folder {FolderId} from device {DeviceId}", folderId, request.DeviceId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get system statistics
    /// </summary>
    [HttpGet("statistics")]
    public async Task<ActionResult<SyncStatistics>> GetStatistics()
    {
        try
        {
            var statistics = await _syncEngine.GetStatisticsAsync();
            return Ok(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting statistics");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}