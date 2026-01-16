using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatioHelper.Agent.Services;

/// <summary>
/// Monitors Syncthing folder synchronization completion
/// Checks when all folders are fully synced with no pending changes
/// </summary>
public class SyncthingCompletionMonitor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncthingCompletionMonitor> _logger;

    public SyncthingCompletionMonitor(IHttpClientFactory httpClientFactory, ILogger<SyncthingCompletionMonitor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Wait for all monitored folders to complete synchronization
    /// Returns true when sync is complete and stable for the specified timeout
    /// Returns false if max wait time exceeded (services should be started anyway)
    /// </summary>
    public async Task<bool> WaitForSyncCompletionAsync(
        List<string> folderIds,
        int idleTimeoutSeconds,
        int pollingIntervalSeconds,
        int requiredStableChecks,
        int maxWaitTimeMinutes,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Monitoring {FolderCount} folders for sync completion", folderIds.Count);

        var pollingInterval = TimeSpan.FromSeconds(pollingIntervalSeconds);
        var stableChecks = 0;
        var lastChangeTime = DateTime.UtcNow;
        var startTime = DateTime.UtcNow;
        var maxWaitTime = TimeSpan.FromMinutes(maxWaitTimeMinutes);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Check if max wait time exceeded
                var elapsedTime = DateTime.UtcNow - startTime;
                if (elapsedTime > maxWaitTime)
                {
                    _logger.LogError("Sync completion timeout after {Minutes} minutes - forcing service start",
                        maxWaitTimeMinutes);
                    return false; // Caller should start services anyway
                }

                bool allFoldersCompleted = true;
                bool anyChangesDetected = false;
                bool apiErrorOccurred = false;

                foreach (var folderId in folderIds)
                {
                    var status = await GetFolderStatusAsync(folderId, cancellationToken);

                    if (status == null)
                    {
                        _logger.LogWarning("Failed to get status for folder {FolderId}", folderId);
                        allFoldersCompleted = false;
                        apiErrorOccurred = true;
                        continue;
                    }

                    // Check if folder needs sync
                    if (status.NeedBytes > 0 || status.NeedFiles > 0 || status.NeedDeletes > 0)
                    {
                        _logger.LogDebug("Folder {FolderId}: needs {NeedFiles} files, {NeedBytes} bytes, {NeedDeletes} deletes",
                            folderId, status.NeedFiles, status.NeedBytes, status.NeedDeletes);
                        allFoldersCompleted = false;
                        anyChangesDetected = true;
                        lastChangeTime = DateTime.UtcNow;
                    }

                    // Check folder state - only "idle" means folder is complete
                    // All other states (scanning, syncing, stopped, empty string, etc.) mean folder is not complete
                    if (status.State != "idle")
                    {
                        _logger.LogDebug("Folder {FolderId}: state = {State} (not idle)", folderId, status.State);
                        allFoldersCompleted = false;
                        anyChangesDetected = true;
                        lastChangeTime = DateTime.UtcNow;
                    }
                }

                // If all folders completed and no changes detected
                if (allFoldersCompleted && !anyChangesDetected)
                {
                    var idleTime = DateTime.UtcNow - lastChangeTime;

                    // Check if we've been idle long enough
                    if (idleTime.TotalSeconds >= idleTimeoutSeconds)
                    {
                        stableChecks++;
                        _logger.LogInformation("Sync stable check {Check}/{Required} (idle for {IdleSeconds}s)",
                            stableChecks, requiredStableChecks, (int)idleTime.TotalSeconds);

                        if (stableChecks >= requiredStableChecks)
                        {
                            _logger.LogInformation("All folders sync completed and stable!");
                            return true;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Waiting for idle timeout: {ElapsedSeconds}/{RequiredSeconds}s",
                            (int)idleTime.TotalSeconds, idleTimeoutSeconds);
                    }
                }
                else if (!apiErrorOccurred)
                {
                    // Only reset stable checks if actual sync activity was detected
                    // Don't reset on temporary API errors to avoid never-ending loops
                    stableChecks = 0;
                }

                await Task.Delay(pollingInterval, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error monitoring sync completion");
                await Task.Delay(pollingInterval, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring sync completion");
                await Task.Delay(pollingInterval, cancellationToken);
            }
        }

        return false;
    }

    /// <summary>
    /// Get folder status from Syncthing API
    /// Endpoint: GET /rest/db/status?folder={folderId}
    /// </summary>
    private async Task<SyncthingFolderStatus?> GetFolderStatusAsync(string folderId, CancellationToken cancellationToken)
    {
        try
        {
            // Note: Do NOT use 'using' with IHttpClientFactory - it manages HttpClient lifecycle and connection pooling
            var httpClient = _httpClientFactory.CreateClient("Syncthing");
            var url = $"/rest/db/status?folder={Uri.EscapeDataString(folderId)}";
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get folder status for {FolderId}: {StatusCode}",
                    folderId, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = JsonSerializer.Deserialize<SyncthingFolderStatus>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder status for {FolderId}", folderId);
            return null;
        }
    }

    /// <summary>
    /// Check if any folder is currently syncing (receiving files)
    /// </summary>
    public async Task<bool> IsAnyFolderSyncingAsync(List<string> folderIds, CancellationToken cancellationToken)
    {
        foreach (var folderId in folderIds)
        {
            var status = await GetFolderStatusAsync(folderId, cancellationToken);
            // Folder is syncing if: has pending data OR state is not idle
            if (status != null && (status.NeedBytes > 0 || status.NeedFiles > 0 || status.NeedDeletes > 0 || status.State != "idle"))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// DTO for Syncthing folder status
/// Maps to /rest/db/status response
/// </summary>
internal class SyncthingFolderStatus
{
    [JsonPropertyName("globalBytes")]
    public long GlobalBytes { get; set; }

    [JsonPropertyName("globalDeleted")]
    public long GlobalDeleted { get; set; }

    [JsonPropertyName("globalDirectories")]
    public int GlobalDirectories { get; set; }

    [JsonPropertyName("globalFiles")]
    public int GlobalFiles { get; set; }

    [JsonPropertyName("globalSymlinks")]
    public int GlobalSymlinks { get; set; }

    [JsonPropertyName("globalTotalItems")]
    public int GlobalTotalItems { get; set; }

    [JsonPropertyName("inSyncBytes")]
    public long InSyncBytes { get; set; }

    [JsonPropertyName("inSyncFiles")]
    public int InSyncFiles { get; set; }

    [JsonPropertyName("invalid")]
    public string Invalid { get; set; } = string.Empty;

    [JsonPropertyName("localBytes")]
    public long LocalBytes { get; set; }

    [JsonPropertyName("localDeleted")]
    public long LocalDeleted { get; set; }

    [JsonPropertyName("localDirectories")]
    public int LocalDirectories { get; set; }

    [JsonPropertyName("localFiles")]
    public int LocalFiles { get; set; }

    [JsonPropertyName("localSymlinks")]
    public int LocalSymlinks { get; set; }

    [JsonPropertyName("localTotalItems")]
    public int LocalTotalItems { get; set; }

    [JsonPropertyName("needBytes")]
    public long NeedBytes { get; set; }

    [JsonPropertyName("needDeletes")]
    public int NeedDeletes { get; set; }

    [JsonPropertyName("needDirectories")]
    public int NeedDirectories { get; set; }

    [JsonPropertyName("needFiles")]
    public int NeedFiles { get; set; }

    [JsonPropertyName("needSymlinks")]
    public int NeedSymlinks { get; set; }

    [JsonPropertyName("needTotalItems")]
    public int NeedTotalItems { get; set; }

    [JsonPropertyName("pullErrors")]
    public int PullErrors { get; set; }

    [JsonPropertyName("receiveOnlyChangedBytes")]
    public long ReceiveOnlyChangedBytes { get; set; }

    [JsonPropertyName("receiveOnlyChangedDeletes")]
    public int ReceiveOnlyChangedDeletes { get; set; }

    [JsonPropertyName("receiveOnlyChangedDirectories")]
    public int ReceiveOnlyChangedDirectories { get; set; }

    [JsonPropertyName("receiveOnlyChangedFiles")]
    public int ReceiveOnlyChangedFiles { get; set; }

    [JsonPropertyName("receiveOnlyChangedSymlinks")]
    public int ReceiveOnlyChangedSymlinks { get; set; }

    [JsonPropertyName("receiveOnlyTotalItems")]
    public int ReceiveOnlyTotalItems { get; set; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("stateChanged")]
    public DateTime StateChanged { get; set; }

    [JsonPropertyName("version")]
    public long Version { get; set; }
}
