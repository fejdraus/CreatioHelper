using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Agent.Services;

public class SyncthingCompletionMonitor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncthingCompletionMonitor> _logger;
    public SyncthingCompletionMonitor(IHttpClientFactory httpClientFactory, ILogger<SyncthingCompletionMonitor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
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
                var elapsedTime = DateTime.UtcNow - startTime;
                if (elapsedTime > maxWaitTime)
                {
                    _logger.LogError("Sync completion timeout after {Minutes} minutes - forcing service start",
                        maxWaitTimeMinutes);
                    return false;
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

                    if (status.NeedBytes > 0 || status.NeedFiles > 0 || status.NeedDeletes > 0)
                    {
                        _logger.LogDebug("Folder {FolderId}: needs {NeedFiles} files, {NeedBytes} bytes, {NeedDeletes} deletes",
                            folderId, status.NeedFiles, status.NeedBytes, status.NeedDeletes);
                        allFoldersCompleted = false;
                        anyChangesDetected = true;
                        lastChangeTime = DateTime.UtcNow;
                    }
                    if (status.State != "idle")
                    {
                        _logger.LogDebug("Folder {FolderId}: state = {State} (not idle)", folderId, status.State);
                        allFoldersCompleted = false;
                        anyChangesDetected = true;
                        lastChangeTime = DateTime.UtcNow;
                    }
                }
                if (allFoldersCompleted && !anyChangesDetected)
                {
                    var idleTime = DateTime.UtcNow - lastChangeTime;
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
        private Task<SyncthingFolderStatus?> GetFolderStatusAsync(string folderId, CancellationToken cancellationToken)
        => SyncthingFolderStatusClient.GetFolderStatusAsync(
            _httpClientFactory, "Syncthing", folderId, _logger, cancellationToken);
        public async Task<bool> IsAnyFolderSyncingAsync(List<string> folderIds, CancellationToken cancellationToken)
    {
        foreach (var folderId in folderIds)
        {
            var status = await GetFolderStatusAsync(folderId, cancellationToken);
            if (status != null && (status.NeedBytes > 0 || status.NeedFiles > 0 || status.NeedDeletes > 0 || status.State != "idle"))
            {
                return true;
            }
        }
        return false;
    }
}