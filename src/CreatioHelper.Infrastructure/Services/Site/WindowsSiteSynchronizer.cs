using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Common;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Site;

public class WindowsSiteSynchronizer : ISiteSynchronizer
{
    private readonly IOutputWriter _output;
    private readonly IIisManager _iisManager;
    private readonly IFileCopyHelper _fileCopyHelper;
    private readonly IServerStatusService _statusService;
    private const int MaxConcurrentCopies = 7;
    private static readonly SemaphoreSlim CopySemaphore = new(MaxConcurrentCopies);

    public WindowsSiteSynchronizer(IOutputWriter output,
        IIisManager iisManager,
        IFileCopyHelper fileCopyHelper,
        IServerStatusService statusService)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _iisManager = iisManager ?? throw new ArgumentNullException(nameof(iisManager));
        _fileCopyHelper = fileCopyHelper ?? throw new ArgumentNullException(nameof(fileCopyHelper));
        _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
    }

    public async Task<bool> SynchronizeAsync(string sitePath, List<ServerInfo> targetServers, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
        if (targetServers == null) throw new ArgumentNullException(nameof(targetServers));

        sitePath = sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!await StopAllServicesAsync(targetServers, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        if (!await VerifyServicesStoppedAsync(targetServers, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        _output.WriteLine("[OK] All pools and websites are stopped.");
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        string confPath = Path.Combine(sitePath, "Terrasoft.WebApp", "conf");
        string configPath = Path.Combine(sitePath, "Terrasoft.WebApp", "Terrasoft.Configuration");
        var copyTasks = targetServers.Select(server => CopyServerFilesAsync(server, confPath, configPath, cancellationToken)).ToList();
        try
        {
            await Task.WhenAll(copyTasks).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            _output.WriteLine("[OK] All files copied and servers started.");
            return true;
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine("[INFO] Synchronization was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Synchronization failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> StopAllServicesAsync(List<ServerInfo> serversToUpdate, CancellationToken cancellationToken)
    {
        var stopTasks = new List<Task<Result>>();
        foreach (var server in serversToUpdate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(server.PoolName))
            {
                stopTasks.Add(_iisManager.StopAppPoolAsync(server.Name ?? "", server.PoolName, cancellationToken));
            }
            if (!string.IsNullOrWhiteSpace(server.SiteName))
            {
                stopTasks.Add(_iisManager.StopWebsiteAsync(server.Name ?? "", server.SiteName, cancellationToken));
            }
        }
        if (stopTasks.Count == 0)
        {
            _output.WriteLine("[WARN] No pools or websites to stop (names not specified).");
            return true;
        }
        Result[] stopResults = await Task.WhenAll(stopTasks).ConfigureAwait(false);
        if (!stopResults.All(result => result.IsSuccess))
        {
            _output.WriteLine("[ERROR] Failed to stop some pools or websites.");
            foreach (var result in stopResults.Where(r => !r.IsSuccess))
            {
                _output.WriteLine($"[ERROR] {result.ErrorMessage}");
            }
            return false;
        }
        return true;
    }

    private async Task<bool> VerifyServicesStoppedAsync(List<ServerInfo> serversToUpdate, CancellationToken cancellationToken)
    {
        _output.WriteLine("[INFO] Verifying that all services are stopped...");
        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
        await _statusService.RefreshMultipleServerStatusAsync(serversToUpdate.ToArray(), cancellationToken).ConfigureAwait(false);
        var failedStops = new List<string>();
        foreach (var server in serversToUpdate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(server.PoolName) && 
                !string.Equals(server.PoolStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
            {
                failedStops.Add($"Pool '{server.PoolName}' on {server.Name}: {server.PoolStatus}");
            }
            if (!string.IsNullOrWhiteSpace(server.SiteName) && 
                !string.Equals(server.SiteStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
            {
                failedStops.Add($"Site '{server.SiteName}' on {server.Name}: {server.SiteStatus}");
            }
        }
        if (failedStops.Count > 0)
        {
            _output.WriteLine("[ERROR] Some services failed to stop completely:");
            foreach (var failure in failedStops)
            {
                _output.WriteLine($"[ERROR] - {failure}");
            }
            return false;
        }
        _output.WriteLine("[OK] All services confirmed stopped.");
        return true;
    }

    private async Task CopyServerFilesAsync(ServerInfo server, string confPath, string configPath, CancellationToken cancellationToken)
    {
        try
        {
            await CopySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(async () =>
                {
                    var networkPath = server.NetworkPath;
                    if (string.IsNullOrEmpty(networkPath))
                    {
                        _output.WriteLine($"[ERROR] Network path is not set for server '{server.Name ?? "Unknown"}'");
                        return;
                    }

                    string destConfPath = Path.Combine(networkPath, "Terrasoft.WebApp", "conf");
                    string destConfigPath = Path.Combine(networkPath, "Terrasoft.WebApp", "Terrasoft.Configuration");

                    _output.WriteLine($"[INFO] Starting to copy files to {server.Name ?? "Unknown"}...");
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    await _fileCopyHelper.CopyAsync(server, confPath, destConfPath, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    await _fileCopyHelper.CopyAsync(server, configPath, destConfigPath, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    _output.WriteLine($"[INFO] File copying to {server.Name} completed, starting services...");
                    
                    bool appPoolStarted = true;
                    bool websiteStarted = true;
                    
                    if (!string.IsNullOrWhiteSpace(server.PoolName))
                    {
                        var startPoolResult = await _iisManager.StartAppPoolAsync(server.Name ?? "", server.PoolName, cancellationToken).ConfigureAwait(false);
                        appPoolStarted = startPoolResult.IsSuccess;
                        if (!appPoolStarted)
                        {
                            _output.WriteLine($"[WARN] Failed to start app pool '{server.PoolName}' on {server.Name ?? "Unknown"}: {startPoolResult.ErrorMessage}");
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(server.SiteName))
                    {
                        var startSiteResult = await _iisManager.StartWebsiteAsync(server.Name ?? "", server.SiteName, cancellationToken).ConfigureAwait(false);
                        websiteStarted = startSiteResult.IsSuccess;
                        if (!websiteStarted)
                        {
                            _output.WriteLine($"[WARN] Failed to start website '{server.SiteName}' on {server.Name ?? "Unknown"}: {startSiteResult.ErrorMessage}");
                        }
                    }
                    if (appPoolStarted && websiteStarted)
                    {
                        await VerifyServerStartedAsync(server, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _output.WriteLine($"[WARN] {server.Name} - File copy completed, but some services failed to start");
                    }
                }, cancellationToken);
            }
            finally
            {
                CopySemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine($"[INFO] Synchronization for {server.Name} was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] File copy operation failed for {server.Name}: {ex.Message}");
            throw;
        }
    }
    
    private async Task VerifyServerStartedAsync(ServerInfo server, CancellationToken cancellationToken)
    {
        _output.WriteLine($"[INFO] Verifying services started on {server.Name}...");
        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
        await _statusService.RefreshServerStatusAsync(server).ConfigureAwait(false);
        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(server.PoolName) && 
            !string.Equals(server.PoolStatus, "Started", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"Pool '{server.PoolName}': {server.PoolStatus}");
        }
        if (!string.IsNullOrWhiteSpace(server.SiteName) && 
            !string.Equals(server.SiteStatus, "Started", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"Site '{server.SiteName}': {server.SiteStatus}");
        }
        if (issues.Count > 0)
        {
            _output.WriteLine($"[WARN] {server.Name} - Some services may not have started properly:");
            foreach (var issue in issues)
            {
                _output.WriteLine($"[WARN]   - {issue}");
            }
        }
        else
        {
            _output.WriteLine($"[OK] {server.Name} - All services confirmed started and running");
        }
    }
}
