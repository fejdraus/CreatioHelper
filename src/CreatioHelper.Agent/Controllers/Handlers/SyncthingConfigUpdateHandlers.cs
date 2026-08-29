using System.Text.Json;
using CreatioHelper.Application.DTOs;
using CreatioHelper.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Agent.Controllers.Handlers;

internal sealed class SyncthingConfigUpdateHandlers
{
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger _logger;
    private readonly Func<Task> _saveConfigurationAsync;

    public SyncthingConfigUpdateHandlers(
        ISyncEngine syncEngine,
        ILogger logger,
        Func<Task> saveConfigurationAsync)
    {
        _syncEngine = syncEngine;
        _logger = logger;
        _saveConfigurationAsync = saveConfigurationAsync;
    }

    public async Task<ActionResult> UpdateConfigAsync(JsonElement config)
    {
        _logger.LogInformation("Received full configuration update, applying changes");

        if (config.TryGetProperty("folders", out var foldersElement) && foldersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var folderJson in foldersElement.EnumerateArray())
            {
                try
                {
                    var folderConfig = ParseFolderConfiguration(folderJson);
                    var existing = await _syncEngine.GetFolderAsync(folderConfig.Id);
                    if (existing == null)
                        await _syncEngine.AddFolderAsync(folderConfig);
                    else
                        await _syncEngine.UpdateFolderAsync(folderConfig);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error applying folder config from PUT /rest/config");
                }
            }
        }

        if (config.TryGetProperty("devices", out var devicesElement) && devicesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var deviceJson in devicesElement.EnumerateArray())
            {
                try
                {
                    var deviceId = deviceJson.GetProperty("deviceID").GetString();
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
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error applying device config from PUT /rest/config");
                }
            }
        }

        await _saveConfigurationAsync();

        return new OkObjectResult(new { success = true });
    }

    private static FolderConfiguration ParseFolderConfiguration(JsonElement json)
    {
        return new FolderConfiguration
        {
            Id = json.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
            Label = json.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? "" : "",
            Path = json.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? "" : "",
            Type = json.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "sendreceive" : "sendreceive"
        };
    }
}
