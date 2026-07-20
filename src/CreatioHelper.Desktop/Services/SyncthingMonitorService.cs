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
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Services;

public class SyncthingMonitorService : ISyncthingMonitorService
{
    private readonly HttpClient _httpClient;
    private readonly IOutputWriter _output;
    private readonly SyncthingRequestFactory _requests;
    public SyncthingMonitorService(
        IHttpClientFactory httpClientFactory,
        IOutputWriter output,
        string apiUrl,
        string? apiKey)
    {
        _httpClient = httpClientFactory.CreateClient("Syncthing");
        _output = output;
        _requests = new SyncthingRequestFactory(apiUrl, apiKey);
    }
    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUrl)
        => _requests.Create(method, relativeUrl);
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

                bool allFoldersCompleted = true;
                var folderStatuses = new List<string>();
                foreach (var folderId in server.SyncthingFolderIds)
                {
                    var completion = await GetRemoteDeviceCompletionAsync(
                        folderId,
                        server.SyncthingDeviceId,
                        cancellationToken);
                    folderStatuses.Add($"{folderId}: {completion.Completion:F1}%");

                    server.UpdateFolderSyncState(
                        folderId,
                        completion.Completion,
                        completion.NeedBytes,
                        completion.NeedItems,
                        completion.RemoteState ?? "unknown");

                    var isFolderCompleted = CheckRemoteCompletionCriteria(completion, server.Name ?? "Unknown");
                    if (!isFolderCompleted)
                    {
                        allFoldersCompleted = false;
                    }
                }

                server.PruneStaleFolderStates();
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
        public async Task<List<ServerInfo>> WaitForMultipleServersAsync(
        List<ServerInfo> servers,
        Func<ServerInfo, Task> onServerCompleted,
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
                    if (onServerCompleted != null)
                    {
                        await onServerCompleted(server);
                    }
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
        public async Task<string> GetDeviceAndFolderStatusAsync(ServerInfo server, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(server.SyncthingDeviceId) || server.SyncthingFolderIds.Count == 0)
        {
            return "⚙️ Not Configured";
        }

        try
        {
            var deviceConnected = await IsDeviceConnectedAsync(server.SyncthingDeviceId, cancellationToken);
            if (!deviceConnected)
            {
                return "❌ Offline";
            }
            double totalCompletion = 0;
            long totalNeedItems = 0;
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
            double avgCompletion = totalCompletion / server.SyncthingFolderIds.Count;
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
        public async Task<bool> PauseFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Patch, $"/rest/config/folders/{Uri.EscapeDataString(folderId)}");
            request.Content = new StringContent(
                "{\"paused\":true}",
                System.Text.Encoding.UTF8,
                "application/json");
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
        public async Task<bool> ResumeFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Patch, $"/rest/config/folders/{Uri.EscapeDataString(folderId)}");
            request.Content = new StringContent(
                "{\"paused\":false}",
                System.Text.Encoding.UTF8,
                "application/json");

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
        private async Task<bool> IsDeviceConnectedAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/rest/system/connections");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var connections = JsonSerializer.Deserialize<SyncthingConnectionsDto>(json, JsonDefaults.CaseInsensitive);

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

        private bool CheckRemoteCompletionCriteria(SyncthingCompletionDto completion, string serverName)
    {
        if (completion.RemoteState != "valid")
        {
            return false;
        }
        if (completion.NeedBytes > 0)
        {
            return false;
        }

        if (completion.NeedItems > 0)
        {
            return false;
        }

        if (completion.NeedDeletes > 0)
        {
            if (completion.Completion < 95.0)
            {
                return false;
            }
        }
        else
        {
            if (completion.Completion < 99.99)
            {
                return false;
            }
        }
        return true;
    }

        private async Task<SyncthingCompletionDto> GetRemoteDeviceCompletionAsync(
        string folderId,
        string remoteDeviceId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get,
            $"/rest/db/completion?folder={Uri.EscapeDataString(folderId)}&device={Uri.EscapeDataString(remoteDeviceId)}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
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
        var completion = JsonSerializer.Deserialize<SyncthingCompletionDto>(json, JsonDefaults.CaseInsensitive);
        return completion ?? new SyncthingCompletionDto();
    }
}
internal class SyncthingCompletionDto
{
    [JsonPropertyName("completion")]
    public double Completion { get; set; }
    [JsonPropertyName("globalBytes")]
    public long GlobalBytes { get; set; }
    [JsonPropertyName("needBytes")]
    public long NeedBytes { get; set; }
    [JsonPropertyName("globalItems")]
    public long GlobalItems { get; set; }
    [JsonPropertyName("needItems")]
    public long NeedItems { get; set; }
    [JsonPropertyName("needDeletes")]
    public long NeedDeletes { get; set; }
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }
        [JsonPropertyName("remoteState")]
    public string RemoteState { get; set; } = string.Empty;
}
internal class SyncthingConnectionsDto
{
    [JsonPropertyName("connections")]
    public Dictionary<string, SyncthingConnectionDto>? Connections { get; set; }
}
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
