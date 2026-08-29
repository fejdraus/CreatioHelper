using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Agent.Controllers.Handlers;

internal sealed class SyncthingSystemConfigHandlers
{
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger _logger;

    public SyncthingSystemConfigHandlers(
        ISyncEngine syncEngine,
        ILogger logger)
    {
        _syncEngine = syncEngine;
        _logger = logger;
    }

    public async Task<ActionResult> UpdateConfigAsync(JsonElement config)
    {
        _logger.LogInformation("Received configuration update, applying changes");

        if (config.TryGetProperty("folders", out var foldersElement) && foldersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var folderJson in foldersElement.EnumerateArray())
            {
                if (!folderJson.TryGetProperty("id", out var idProp)) { continue; }
                var folderId = idProp.GetString();
                if (string.IsNullOrEmpty(folderId)) { continue; }

                var existing = await _syncEngine.GetFolderAsync(folderId);
                if (existing == null)
                {
                    var path = folderJson.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? "" : "";
                    var label = folderJson.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? folderId : folderId;
                    var type = folderJson.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "sendreceive" : "sendreceive";
                    if (!string.IsNullOrEmpty(path))
                    {
                        await _syncEngine.AddFolderAsync(folderId, label, path, type);
                    }
                }
            }
        }

        if (config.TryGetProperty("devices", out var devicesElement) && devicesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var deviceJson in devicesElement.EnumerateArray())
            {
                if (!deviceJson.TryGetProperty("deviceID", out var deviceIdProp)) { continue; }
                var deviceId = deviceIdProp.GetString();
                if (string.IsNullOrEmpty(deviceId)) { continue; }

                var devices = await _syncEngine.GetDevicesAsync();
                if (!devices.Any(d => d.DeviceId == deviceId))
                {
                    var name = deviceJson.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? deviceId : deviceId;
                    var addresses = new List<string> { "dynamic" };
                    if (deviceJson.TryGetProperty("addresses", out var addrProp) && addrProp.ValueKind == JsonValueKind.Array)
                    {
                        addresses = addrProp.EnumerateArray()
                            .Select(a => a.GetString())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Cast<string>()
                            .ToList();
                    }
                    await _syncEngine.AddDeviceAsync(deviceId, name, null, addresses);
                }
            }
        }

        return new OkObjectResult(new { success = true });
    }
}
