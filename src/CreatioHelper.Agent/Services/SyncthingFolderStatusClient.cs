using System.Text.Json;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Agent.Services;

internal static class SyncthingFolderStatusClient
{
    public static async Task<SyncthingFolderStatus?> GetFolderStatusAsync(
        IHttpClientFactory httpClientFactory,
        string httpClientName,
        string folderId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(httpClientName);
            var url = $"/rest/db/status?folder={Uri.EscapeDataString(folderId)}";
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to get folder status for {FolderId}: {StatusCode}",
                    folderId, response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SyncthingFolderStatus>(json, JsonDefaults.CaseInsensitive);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting folder status for {FolderId}", folderId);
            return null;
        }
    }
}