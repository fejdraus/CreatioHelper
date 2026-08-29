using CreatioHelper.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Agent.Controllers.Handlers;

internal sealed class SyncthingConfigDeviceHandlers
{
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger _logger;

    public SyncthingConfigDeviceHandlers(
        ISyncEngine syncEngine,
        ILogger logger)
    {
        _syncEngine = syncEngine;
        _logger = logger;
    }

    public async Task<ActionResult> DeleteDeviceAsync(string id)
    {
        var result = await _syncEngine.RemoveDeviceAsync(id);
        if (!result)
            return new NotFoundObjectResult(new { error = $"Device {id} not found or cannot be removed" });

        _logger.LogInformation("Device {DeviceId} deleted via API", id);
        return new OkObjectResult(new { message = $"Device {id} removed successfully" });
    }
}
