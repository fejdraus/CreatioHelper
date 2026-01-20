using CreatioHelper.Agent.Authorization;
using CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Syncthing-compatible /rest/cluster API endpoints
/// Provides 100% compatibility with Syncthing cluster/pending API
/// </summary>
[ApiController]
[Route("rest/cluster")]
public class SyncthingClusterController : ControllerBase
{
    private readonly IPendingService _pendingService;
    private readonly ILogger<SyncthingClusterController> _logger;

    public SyncthingClusterController(IPendingService pendingService, ILogger<SyncthingClusterController> logger)
    {
        _pendingService = pendingService;
        _logger = logger;
    }

    /// <summary>
    /// Get pending devices - 100% Syncthing compatible
    /// GET /rest/cluster/pending/devices
    /// </summary>
    [HttpGet("pending/devices")]
    [Authorize(Roles = Roles.ReadRoles)]
    public ActionResult<object> GetPendingDevices()
    {
        try
        {
            var pendingDevices = _pendingService.GetPendingDevices();

            // Build Syncthing-compatible response: Dictionary<deviceId, PendingDeviceInfo>
            var result = new Dictionary<string, object>();

            foreach (var device in pendingDevices)
            {
                result[device.DeviceId] = new
                {
                    time = device.DiscoveredAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    name = device.Name,
                    address = device.Address ?? string.Empty
                };
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pending devices");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Delete (reject) pending device - 100% Syncthing compatible
    /// DELETE /rest/cluster/pending/devices?device=DEVICE-ID
    /// </summary>
    [HttpDelete("pending/devices")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult DeletePendingDevice([FromQuery] string device)
    {
        try
        {
            if (string.IsNullOrEmpty(device))
                return BadRequest(new { error = "device parameter required" });

            var rejected = _pendingService.RejectPendingDevice(device);

            if (rejected)
            {
                _logger.LogInformation("Rejected pending device {DeviceId}", device);
                return Ok(new { ok = "pending device removed" });
            }
            else
            {
                return NotFound(new { error = "pending device not found" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting pending device {Device}", device);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get pending folders - 100% Syncthing compatible
    /// GET /rest/cluster/pending/folders
    /// </summary>
    [HttpGet("pending/folders")]
    [Authorize(Roles = Roles.ReadRoles)]
    public ActionResult<object> GetPendingFolders()
    {
        try
        {
            var pendingFolders = _pendingService.GetPendingFolders();

            // Build Syncthing-compatible response: Dictionary<deviceId, Dictionary<folderId, PendingFolderInfo>>
            var result = new Dictionary<string, Dictionary<string, object>>();

            foreach (var folder in pendingFolders)
            {
                if (!result.ContainsKey(folder.OfferedByDeviceId))
                {
                    result[folder.OfferedByDeviceId] = new Dictionary<string, object>();
                }

                result[folder.OfferedByDeviceId][folder.FolderId] = new
                {
                    time = folder.OfferedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    label = folder.Label,
                    receiveEncrypted = folder.ReceiveEncrypted,
                    remoteEncrypted = folder.IsEncrypted
                };
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pending folders");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Delete (reject) pending folder - 100% Syncthing compatible
    /// DELETE /rest/cluster/pending/folders?device=DEVICE-ID&amp;folder=FOLDER-ID
    /// </summary>
    [HttpDelete("pending/folders")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult DeletePendingFolder([FromQuery] string device, [FromQuery] string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(folder))
                return BadRequest(new { error = "device and folder parameters required" });

            var rejected = _pendingService.RejectPendingFolder(folder, device);

            if (rejected)
            {
                _logger.LogInformation("Rejected pending folder {FolderId} from device {DeviceId}", folder, device);
                return Ok(new { ok = "pending folder removed" });
            }
            else
            {
                return NotFound(new { error = "pending folder not found" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting pending folder {Folder} from device {Device}", folder, device);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
