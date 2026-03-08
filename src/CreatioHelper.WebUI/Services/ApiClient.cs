using System.Net.Http.Json;
using CreatioHelper.WebUI.Models;

namespace CreatioHelper.WebUI.Services;

/// <summary>
/// REST API client for communicating with the CreatioHelper Agent
/// </summary>
public interface IApiClient
{
    // System
    Task<SystemStatus?> GetSystemStatusAsync();
    Task<SystemConfig?> GetConfigAsync();
    Task RestartAsync();
    Task ShutdownAsync();
    Task<string?> GetVersionAsync();
    Task<SystemVersionInfo?> GetVersionInfoAsync();

    // User Management
    Task<UserInfo[]> GetUsersAsync();
    Task<UserInfo?> CreateUserAsync(CreateUserModel model);
    Task<UserInfo?> UpdateUserAsync(string username, UpdateUserModel model);
    Task<bool> DeleteUserAsync(string username);
    Task<bool> ChangePasswordAsync(string username, ChangePasswordModel model);

    // Folders
    Task<FolderConfig[]> GetFoldersAsync();
    Task<FolderStatus?> GetFolderStatusAsync(string folderId);
    Task<FileError[]> GetFolderErrorsAsync(string folderId);
    Task ScanFolderAsync(string folderId, string? subPath = null);
    Task UpdateFolderAsync(FolderConfig folder);
    Task AddFolderAsync(FolderConfig folder);
    Task DeleteFolderAsync(string folderId);
    Task<string?> GetIgnoresAsync(string folderId);
    Task<IgnoresInfo> GetIgnoresInfoAsync(string folderId);
    Task SetIgnoresAsync(string folderId, string[] patterns);
    Task RevertFolderAsync(string folderId);

    // Devices
    Task<DeviceConfig[]> GetDevicesAsync();
    Task<DeviceStats?> GetDeviceStatsAsync(string deviceId);
    Task<ConnectionInfo[]> GetConnectionsAsync();
    Task UpdateDeviceAsync(DeviceConfig device);
    Task AddDeviceAsync(DeviceConfig device);
    Task DeleteDeviceAsync(string deviceId);
    Task<PendingDevice[]> GetPendingDevicesAsync();
    Task AcceptDeviceAsync(string deviceId);
    Task RejectDeviceAsync(string deviceId);

    // Events
    Task<SyncEvent[]> GetEventsAsync(int since = 0, int limit = 100, string? filter = null);

    // Discovery & Listeners
    Task<DiscoveryStatus?> GetDiscoveryStatusAsync();
    Task<ListenersStatus> GetListenersStatusAsync();

    // Debug
    Task<DebugInfo?> GetDebugInfoAsync();
    Task<string?> GetLogAsync(int lines = 100);
    Task<byte[]?> GetSupportBundleAsync();

    // Config update
    Task UpdateConfigAsync(SystemConfig config);

    // System logs
    Task<LogEntry[]> GetSystemLogsAsync(int limit = 100);

    // Profiling/Debug actions
    Task TriggerGCAsync();
    Task<byte[]> GetHeapDumpAsync();
    Task<byte[]> GetGoroutineDumpAsync();
    Task<byte[]> GetCpuProfileAsync(int duration = 30);

    // Advanced settings actions
    Task ResetDatabaseAsync();
    Task FactoryResetAsync();

    // Support bundle
    Task<string> GenerateSupportBundlePreviewAsync(object options);
    Task<byte[]> GenerateSupportBundleAsync(object options);
}

public class ApiClient : IApiClient
{
    private readonly HttpClient _httpClient;

    public ApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    #region System

    public async Task<SystemStatus?> GetSystemStatusAsync()
    {
        return await _httpClient.GetFromJsonAsync<SystemStatus>("/rest/system/status");
    }

    public async Task<SystemConfig?> GetConfigAsync()
    {
        return await _httpClient.GetFromJsonAsync<SystemConfig>("/rest/config");
    }

    public async Task RestartAsync()
    {
        await _httpClient.PostAsync("/rest/system/restart", null);
    }

    public async Task ShutdownAsync()
    {
        await _httpClient.PostAsync("/rest/system/shutdown", null);
    }

    public async Task<string?> GetVersionAsync()
    {
        var result = await _httpClient.GetFromJsonAsync<VersionInfo>("/rest/system/version");
        return result?.Version;
    }

    public async Task<SystemVersionInfo?> GetVersionInfoAsync()
    {
        var result = await _httpClient.GetFromJsonAsync<VersionInfo>("/rest/system/version");
        if (result == null) return null;

        return new SystemVersionInfo
        {
            Version = result.Version,
            Os = result.Os,
            Arch = result.Arch,
            LongVersion = result.LongVersion
        };
    }

    #endregion

    #region Folders

    public async Task<FolderConfig[]> GetFoldersAsync()
    {
        return await _httpClient.GetFromJsonAsync<FolderConfig[]>("/rest/config/folders") ?? [];
    }

    public async Task<FolderStatus?> GetFolderStatusAsync(string folderId)
    {
        return await _httpClient.GetFromJsonAsync<FolderStatus>($"/rest/db/status?folder={Uri.EscapeDataString(folderId)}");
    }

    public async Task<FileError[]> GetFolderErrorsAsync(string folderId)
    {
        return await _httpClient.GetFromJsonAsync<FileError[]>($"/rest/folder/errors?folder={Uri.EscapeDataString(folderId)}") ?? [];
    }

    public async Task ScanFolderAsync(string folderId, string? subPath = null)
    {
        var url = $"/rest/db/scan?folder={Uri.EscapeDataString(folderId)}";
        if (!string.IsNullOrEmpty(subPath))
        {
            url += $"&sub={Uri.EscapeDataString(subPath)}";
        }
        await _httpClient.PostAsync(url, null);
    }

    public async Task UpdateFolderAsync(FolderConfig folder)
    {
        await _httpClient.PutAsJsonAsync($"/rest/config/folders/{Uri.EscapeDataString(folder.Id)}", folder);
    }

    public async Task AddFolderAsync(FolderConfig folder)
    {
        await _httpClient.PostAsJsonAsync("/rest/config/folders", folder);
    }

    public async Task DeleteFolderAsync(string folderId)
    {
        await _httpClient.DeleteAsync($"/rest/config/folders/{Uri.EscapeDataString(folderId)}");
    }

    public async Task<string?> GetIgnoresAsync(string folderId)
    {
        var result = await _httpClient.GetFromJsonAsync<IgnoresInfo>($"/rest/db/ignores?folder={Uri.EscapeDataString(folderId)}");
        return result?.Ignore != null ? string.Join("\n", result.Ignore) : null;
    }

    public async Task<IgnoresInfo> GetIgnoresInfoAsync(string folderId)
    {
        return await _httpClient.GetFromJsonAsync<IgnoresInfo>($"/rest/db/ignores?folder={Uri.EscapeDataString(folderId)}")
            ?? new IgnoresInfo();
    }

    public async Task SetIgnoresAsync(string folderId, string[] patterns)
    {
        await _httpClient.PostAsJsonAsync($"/rest/db/ignores?folder={Uri.EscapeDataString(folderId)}", new { ignore = patterns });
    }

    public async Task RevertFolderAsync(string folderId)
    {
        await _httpClient.PostAsync($"/rest/db/revert?folder={Uri.EscapeDataString(folderId)}", null);
    }

    #endregion

    #region Devices

    public async Task<DeviceConfig[]> GetDevicesAsync()
    {
        return await _httpClient.GetFromJsonAsync<DeviceConfig[]>("/rest/config/devices") ?? [];
    }

    public async Task<DeviceStats?> GetDeviceStatsAsync(string deviceId)
    {
        return await _httpClient.GetFromJsonAsync<DeviceStats>($"/rest/stats/device?device={Uri.EscapeDataString(deviceId)}");
    }

    public async Task<ConnectionInfo[]> GetConnectionsAsync()
    {
        var result = await _httpClient.GetFromJsonAsync<ConnectionsResponse>("/rest/system/connections");
        return result?.Connections?.Values.ToArray() ?? [];
    }

    public async Task UpdateDeviceAsync(DeviceConfig device)
    {
        await _httpClient.PutAsJsonAsync($"/rest/config/devices/{Uri.EscapeDataString(device.DeviceId)}", device);
    }

    public async Task AddDeviceAsync(DeviceConfig device)
    {
        await _httpClient.PostAsJsonAsync("/rest/config/devices", device);
    }

    public async Task DeleteDeviceAsync(string deviceId)
    {
        await _httpClient.DeleteAsync($"/rest/config/devices/{Uri.EscapeDataString(deviceId)}");
    }

    public async Task<PendingDevice[]> GetPendingDevicesAsync()
    {
        // Syncthing returns dictionary: { "DEVICE-ID": { time, name, address } }
        var result = await _httpClient.GetFromJsonAsync<Dictionary<string, PendingDeviceInfo>>("/rest/cluster/pending/devices");
        if (result == null) return [];

        return result.Select(kvp => new PendingDevice
        {
            DeviceId = kvp.Key,
            Name = kvp.Value.Name ?? string.Empty,
            Address = kvp.Value.Address ?? string.Empty,
            Time = kvp.Value.Time
        }).ToArray();
    }

    public async Task AcceptDeviceAsync(string deviceId)
    {
        await _httpClient.PostAsync($"/rest/cluster/pending/devices/{Uri.EscapeDataString(deviceId)}/accept", null);
    }

    public async Task RejectDeviceAsync(string deviceId)
    {
        await _httpClient.DeleteAsync($"/rest/cluster/pending/devices/{Uri.EscapeDataString(deviceId)}");
    }

    #endregion

    #region Events

    public async Task<SyncEvent[]> GetEventsAsync(int since = 0, int limit = 100, string? filter = null)
    {
        // Use timeout=0 to get immediate response without long-polling
        var url = $"/rest/events?since={since}&limit={limit}&timeout=0";
        if (!string.IsNullOrEmpty(filter))
        {
            url += $"&events={Uri.EscapeDataString(filter)}";
        }
        return await _httpClient.GetFromJsonAsync<SyncEvent[]>(url) ?? [];
    }

    #endregion

    #region Discovery

    public async Task<DiscoveryStatus?> GetDiscoveryStatusAsync()
    {
        return await _httpClient.GetFromJsonAsync<DiscoveryStatus>("/rest/system/discovery");
    }

    public async Task<ListenersStatus> GetListenersStatusAsync()
    {
        var result = new ListenersStatus();
        try
        {
            var systemStatus = await GetSystemStatusAsync();
            if (systemStatus?.ConnectionServiceStatus != null)
            {
                foreach (var (address, value) in systemStatus.ConnectionServiceStatus)
                {
                    string? error = null;
                    if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        value.TryGetProperty("error", out var errorProp) &&
                        errorProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        error = errorProp.GetString();
                    }

                    result.Listeners.Add(new ListenerInfo
                    {
                        Address = address,
                        Status = string.IsNullOrEmpty(error) ? "ok" : "error",
                        Error = error
                    });
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return result;
    }

    #endregion

    #region Debug

    public async Task<DebugInfo?> GetDebugInfoAsync()
    {
        return await _httpClient.GetFromJsonAsync<DebugInfo>("/rest/system/debug");
    }

    public async Task<string?> GetLogAsync(int lines = 100)
    {
        var result = await _httpClient.GetFromJsonAsync<LogResponse>($"/rest/system/log?lines={lines}");
        return result?.Log;
    }

    public async Task<byte[]?> GetSupportBundleAsync()
    {
        var response = await _httpClient.GetAsync("/rest/debug/support");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsByteArrayAsync();
        }
        return null;
    }

    #endregion

    #region Config Update

    public async Task UpdateConfigAsync(SystemConfig config)
    {
        await _httpClient.PutAsJsonAsync("/rest/config", config);
    }

    #endregion

    #region System Logs

    public async Task<LogEntry[]> GetSystemLogsAsync(int limit = 100)
    {
        return await _httpClient.GetFromJsonAsync<LogEntry[]>($"/rest/system/log/entries?limit={limit}") ?? [];
    }

    #endregion

    #region Profiling/Debug Actions

    public async Task TriggerGCAsync()
    {
        await _httpClient.PostAsync("/rest/debug/gc", null);
    }

    public async Task<byte[]> GetHeapDumpAsync()
    {
        var response = await _httpClient.GetAsync("/rest/debug/heapdump");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<byte[]> GetGoroutineDumpAsync()
    {
        var response = await _httpClient.GetAsync("/rest/debug/goroutines");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<byte[]> GetCpuProfileAsync(int duration = 30)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration + 30));
        var response = await _httpClient.GetAsync($"/rest/debug/cpuprof?duration={duration}", cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    #endregion

    #region Advanced Settings Actions

    public async Task ResetDatabaseAsync()
    {
        await _httpClient.PostAsync("/rest/system/reset?database=true", null);
    }

    public async Task FactoryResetAsync()
    {
        await _httpClient.PostAsync("/rest/system/reset?factory=true", null);
    }

    #endregion

    #region Support Bundle

    public async Task<string> GenerateSupportBundlePreviewAsync(object options)
    {
        var response = await _httpClient.PostAsJsonAsync("/rest/debug/support/preview", options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<byte[]> GenerateSupportBundleAsync(object options)
    {
        var response = await _httpClient.PostAsJsonAsync("/rest/debug/support", options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    #endregion

    #region User Management

    public async Task<UserInfo[]> GetUsersAsync()
    {
        return await _httpClient.GetFromJsonAsync<UserInfo[]>("/api/auth/users") ?? [];
    }

    public async Task<UserInfo?> CreateUserAsync(CreateUserModel model)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/users", model);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserInfo>();
    }

    public async Task<UserInfo?> UpdateUserAsync(string username, UpdateUserModel model)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/auth/users/{Uri.EscapeDataString(username)}", model);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserInfo>();
    }

    public async Task<bool> DeleteUserAsync(string username)
    {
        var response = await _httpClient.DeleteAsync($"/api/auth/users/{Uri.EscapeDataString(username)}");
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ChangePasswordAsync(string username, ChangePasswordModel model)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/auth/users/{Uri.EscapeDataString(username)}/password", new
        {
            currentPassword = model.CurrentPassword,
            newPassword = model.NewPassword
        });
        return response.IsSuccessStatusCode;
    }

    #endregion
}

// Helper classes for API responses
internal class VersionInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("version")]
    public string? Version { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("os")]
    public string? Os { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("arch")]
    public string? Arch { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("longVersion")]
    public string? LongVersion { get; set; }
}

public class IgnoresInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("ignore")]
    public string[]? Ignore { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lines")]
    public string[]? Lines { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("expanded")]
    public string[]? Expanded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; set; }
}

internal class ConnectionsResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("connections")]
    public Dictionary<string, ConnectionInfo>? Connections { get; set; }
}

internal class LogResponse
{
    public string? Log { get; set; }
}

internal class PendingDeviceInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("time")]
    public DateTime Time { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("address")]
    public string? Address { get; set; }
}
