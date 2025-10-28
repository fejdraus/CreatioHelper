using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using Microsoft.Extensions.Http;

namespace CreatioHelper.Services;

/// <summary>
/// Service for monitoring Syncthing synchronization completion for remote servers
/// Uses LOCAL Syncthing API to track REMOTE device completion status
/// Based on Syncthing GUI implementation (syncthingController.js)
/// </summary>
public class SyncthingMonitorService : ISyncthingMonitorService
{
    private readonly HttpClient _httpClient;
    private readonly IOutputWriter _output;
    private readonly string _apiUrl;

    public SyncthingMonitorService(
        IHttpClientFactory httpClientFactory,
        IOutputWriter output,
        string apiUrl,
        string? apiKey)
    {
        _httpClient = httpClientFactory.CreateClient("Syncthing");
        _output = output;
        _apiUrl = apiUrl;

        _httpClient.BaseAddress = new Uri(_apiUrl);
        if (!string.IsNullOrEmpty(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }
    }

    /// <summary>
    /// Wait for synchronization completion for a single server
    /// Queries LOCAL Syncthing API for REMOTE device completion status
    /// </summary>
    public async Task<bool> WaitForServerSyncCompletionAsync(
        ServerInfo server,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(server.SyncthingDeviceId))
        {
            _output.WriteLine($"[WARNING] Server {server.Name} has no Syncthing device ID configured");
            return false;
        }

        if (server.SyncthingFolderIds.Count == 0)
        {
            _output.WriteLine($"[WARNING] Server {server.Name} has no Syncthing folder IDs configured");
            return false;
        }
        var folderIdsStr = string.Join(", ", server.SyncthingFolderIds);
        _output.WriteLine($"       Folders: [{folderIdsStr}], Device: {server.SyncthingDeviceId}");

        var pollingInterval = TimeSpan.FromSeconds(5);
        var stableChecks = 0;
        const int requiredStableChecks = 2;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Query LOCAL Syncthing about REMOTE device completion for ALL folders
                bool allFoldersCompleted = true;
                var folderStatuses = new List<string>();

                foreach (var folderId in server.SyncthingFolderIds)
                {
                    var completion = await GetRemoteDeviceCompletionAsync(
                        folderId,
                        server.SyncthingDeviceId,
                        cancellationToken);

                    folderStatuses.Add($"{folderId}: {completion.Completion:F1}%");

                    // Update folder-specific state in server
                    server.UpdateFolderSyncState(
                        folderId,
                        completion.Completion,
                        completion.NeedBytes,
                        completion.NeedItems,
                        completion.RemoteState ?? "unknown");

                    // Check completion criteria for this folder
                    var isFolderCompleted = CheckRemoteCompletionCriteria(completion, server.Name ?? "Unknown");
                    if (!isFolderCompleted)
                    {
                        allFoldersCompleted = false;
                    }
                }

                // Check if ALL folders completed
                if (allFoldersCompleted && server.AreAllFoldersSynced())
                {
                    stableChecks++;

                    if (stableChecks >= requiredStableChecks)
                    {
                        _output.WriteLine($"[OK] Server {server.Name} ALL folders sync completed!");
                        return true;
                    }

                    await Task.Delay(2000, cancellationToken);
                }
                else
                {
                    stableChecks = 0;
                    await Task.Delay(pollingInterval, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Error monitoring server {server.Name}: {ex.Message}");
                await Task.Delay(pollingInterval, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>
    /// Monitor multiple servers in parallel and invoke callback when each completes
    /// </summary>
    public async Task<List<ServerInfo>> WaitForMultipleServersAsync(
        List<ServerInfo> servers,
        Action<ServerInfo> onServerCompleted,
        CancellationToken cancellationToken)
    {
        var completedServers = new ConcurrentBag<ServerInfo>();

        var tasks = servers.Select(server => Task.Run(async () =>
        {
            try
            {
                var completed = await WaitForServerSyncCompletionAsync(server, cancellationToken);
                if (completed)
                {
                    completedServers.Add(server);
                    onServerCompleted?.Invoke(server);
                }
                else
                {
                    _output.WriteLine($"[WARNING] Server {server.Name} sync monitoring cancelled or failed");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Exception monitoring server {server.Name}: {ex.Message}");
            }
        }, cancellationToken));

        await Task.WhenAll(tasks);

        return completedServers.ToList();
    }

    /// <summary>
    /// Get current status of a Syncthing device and folder
    /// Returns status string with icon: "✅ Up to Date", "🔄 Syncing", etc.
    /// </summary>
    public async Task<string> GetDeviceAndFolderStatusAsync(ServerInfo server, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(server.SyncthingDeviceId) || server.SyncthingFolderIds.Count == 0)
        {
            return "⚙️ Not Configured";
        }

        try
        {
            // Get device connection status
            var deviceConnected = await IsDeviceConnectedAsync(server.SyncthingDeviceId, cancellationToken);
            if (!deviceConnected)
            {
                return "❌ Offline";
            }

            // Check all folders
            double totalCompletion = 0;
            int totalNeedItems = 0;
            long totalNeedBytes = 0;
            bool anyPaused = false;
            bool anyNotSharing = false;
            bool anyInvalid = false;

            foreach (var folderId in server.SyncthingFolderIds)
            {
                var completion = await GetRemoteDeviceCompletionAsync(
                    folderId,
                    server.SyncthingDeviceId,
                    cancellationToken);

                if (completion.RemoteState == "paused")
                {
                    anyPaused = true;
                }
                else if (completion.RemoteState == "notSharing")
                {
                    anyNotSharing = true;
                }
                else if (completion.RemoteState != "valid")
                {
                    anyInvalid = true;
                }

                totalCompletion += completion.Completion;
                totalNeedBytes += completion.NeedBytes;
                totalNeedItems += completion.NeedItems;
            }

            // Calculate average completion
            double avgCompletion = totalCompletion / server.SyncthingFolderIds.Count;

            // Return status based on aggregated state
            if (anyPaused)
            {
                return "⏸️ Paused";
            }
            if (anyNotSharing)
            {
                return "🚫 Not Sharing";
            }
            if (anyInvalid)
            {
                return "❓ Unknown";
            }
            if (totalNeedBytes > 0 || totalNeedItems > 0)
            {
                return $"🔄 Syncing ({avgCompletion:F1}%)";
            }
            return "✅ Up to Date";
        }
        catch (HttpRequestException)
        {
            return "⚠️ API Error";
        }
        catch (Exception ex)
        {
            return $"❌ Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Pause synchronization for a specific folder
    /// Uses PATCH /rest/config/folders/{folder-id} with { "paused": true }
    /// </summary>
    public async Task<bool> PauseFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/rest/config/folders/{Uri.EscapeDataString(folderId)}";
            var jsonContent = new StringContent(
                "{\"paused\":true}",
                System.Text.Encoding.UTF8,
                "application/json");

            var request = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = jsonContent
            };

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            _output.WriteLine($"[OK] Paused folder: {folderId}");
            return true;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to pause folder {folderId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resume synchronization for a specific folder
    /// Uses PATCH /rest/config/folders/{folder-id} with { "paused": false }
    /// </summary>
    public async Task<bool> ResumeFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/rest/config/folders/{Uri.EscapeDataString(folderId)}";
            var jsonContent = new StringContent(
                "{\"paused\":false}",
                System.Text.Encoding.UTF8,
                "application/json");

            var request = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = jsonContent
            };

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            _output.WriteLine($"[OK] Resumed folder: {folderId}");
            return true;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to resume folder {folderId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if a device is currently connected
    /// Endpoint: GET /rest/system/connections
    /// </summary>
    private async Task<bool> IsDeviceConnectedAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var url = "/rest/system/connections";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var connections = JsonSerializer.Deserialize<SyncthingConnectionsDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (connections?.Connections != null && connections.Connections.TryGetValue(deviceId, out var connection))
            {
                return connection.Connected;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check completion criteria for REMOTE device (same logic as Syncthing GUI)
    /// Based on syncthingController.js line 1161: completion._total === 100
    /// </summary>
    private bool CheckRemoteCompletionCriteria(SyncthingCompletionDto completion, string serverName)
    {
        // 1. RemoteState should be "valid" (device connected and syncing)
        if (completion.RemoteState != "valid")
        {
            return false;
        }

        // 2. NeedBytes should be 0 (remote device received all files)
        if (completion.NeedBytes > 0)
        {
            return false;
        }

        // 3. NeedItems should be 0 (all files synchronized)
        if (completion.NeedItems > 0)
        {
            return false;
        }

        // 4. Handle Syncthing's special case with deletes (see syncthingController.js:674-678)
        if (completion.NeedDeletes > 0)
        {
            // If there are deletes, completion will be 95%
            if (completion.Completion < 95.0)
            {
                return false;
            }
        }
        else
        {
            // Without deletes, require >= 99.99%
            if (completion.Completion < 99.99)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Get completion for REMOTE device via LOCAL Syncthing API
    /// Endpoint: GET /rest/db/completion?folder={folder}&device={remoteDeviceId}
    /// </summary>
    private async Task<SyncthingCompletionDto> GetRemoteDeviceCompletionAsync(
        string folderId,
        string remoteDeviceId,
        CancellationToken cancellationToken)
    {
        var url = $"/rest/db/completion?folder={Uri.EscapeDataString(folderId)}&device={Uri.EscapeDataString(remoteDeviceId)}";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (errorText.Contains("folder is paused", StringComparison.OrdinalIgnoreCase))
            {
                return new SyncthingCompletionDto { RemoteState = "paused" };
            }
            response.EnsureSuccessStatusCode();
        }
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var completion = JsonSerializer.Deserialize<SyncthingCompletionDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return completion ?? new SyncthingCompletionDto();
    }
}

/// <summary>
/// DTO for Syncthing completion response
/// Maps to Syncthing API response from /rest/db/completion
/// </summary>
internal class SyncthingCompletionDto
{
    [JsonPropertyName("completion")]
    public double Completion { get; set; }

    [JsonPropertyName("globalBytes")]
    public long GlobalBytes { get; set; }

    [JsonPropertyName("needBytes")]
    public long NeedBytes { get; set; }

    [JsonPropertyName("globalItems")]
    public int GlobalItems { get; set; }

    [JsonPropertyName("needItems")]
    public int NeedItems { get; set; }

    [JsonPropertyName("needDeletes")]
    public int NeedDeletes { get; set; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    /// <summary>
    /// Remote device state: "valid", "paused", "notSharing", "unknown"
    /// </summary>
    [JsonPropertyName("remoteState")]
    public string RemoteState { get; set; } = string.Empty;
}

/// <summary>
/// DTO for Syncthing connections response
/// Maps to Syncthing API response from /rest/system/connections
/// </summary>
internal class SyncthingConnectionsDto
{
    [JsonPropertyName("connections")]
    public Dictionary<string, SyncthingConnectionDto>? Connections { get; set; }
}

/// <summary>
/// DTO for single device connection info
/// </summary>
internal class SyncthingConnectionDto
{
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
