using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Core.Services;

namespace CreatioHelper.Core
{
    [SupportedOSPlatform("windows")]
    public class RemoteSynchronizationService : IRemoteSynchronizationService
    {
        private readonly IOutputWriter _output;
        private readonly ServerStatusService _statusService;
        private const int MaxConcurrentCopies = 7;
        private static readonly SemaphoreSlim CopySemaphore = new(MaxConcurrentCopies);

        public RemoteSynchronizationService(IOutputWriter output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _statusService = new ServerStatusService(output);
        }

        public async Task<bool> SynchronizeAsync(string sitePath, List<ServerInfo> targetServers, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
            if (targetServers == null) throw new ArgumentNullException(nameof(targetServers));

            sitePath = sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string currentServer = Environment.MachineName;
            var serversToUpdate = targetServers.Where(s => !s.Name.Equals(currentServer, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!await StopAllServicesAsync(targetServers, cancellationToken))
            {
                return false;
            }
            if (!await VerifyServicesStoppedAsync(targetServers, cancellationToken))
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
                await Task.WhenAll(copyTasks);
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
            var stopTasks = new List<Task<bool>>();
            foreach (var server in serversToUpdate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                var manager = new RemoteIisManager(_output);
                if (!string.IsNullOrWhiteSpace(server.PoolName))
                {
                    stopTasks.Add(manager.StopAppPoolAsync(server));
                }
                if (!string.IsNullOrWhiteSpace(server.SiteName))
                {
                    stopTasks.Add(manager.StopWebsiteAsync(server));
                }
            }
            if (stopTasks.Count == 0)
            {
                _output.WriteLine("[WARN] No pools or websites to stop (names not specified).");
                return true;
            }
            bool[] stopResults = await Task.WhenAll(stopTasks);
            if (!stopResults.All(result => result))
            {
                _output.WriteLine("[ERROR] Failed to stop some pools or websites.");
                return false;
            }
            return true;
        }

        private async Task<bool> VerifyServicesStoppedAsync(List<ServerInfo> serversToUpdate, CancellationToken cancellationToken)
        {
            _output.WriteLine("[INFO] Verifying that all services are stopped...");
            await Task.Delay(3000, cancellationToken);
            await _statusService.RefreshMultipleServersStatusAsync(serversToUpdate.ToArray());
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
                await CopySemaphore.WaitAsync(cancellationToken);
                try
                {
                    await Task.Run(async () =>
                    {
                        string destConfPath = Path.Combine(server.NetworkPath, "Terrasoft.WebApp", "conf");
                        string destConfigPath = Path.Combine(server.NetworkPath, "Terrasoft.WebApp", "Terrasoft.Configuration");

                        _output.WriteLine($"[INFO] Starting to copy files to {server.Name}...");
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        await FileCopyHelper.CopyAsync(server, confPath, destConfPath, _output, cancellationToken);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        await FileCopyHelper.CopyAsync(server, configPath, destConfigPath, _output, cancellationToken);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        _output.WriteLine($"[INFO] File copying to {server.Name} completed, starting services...");
                        var manager = new RemoteIisManager(_output);
                        bool appPoolStarted = true;
                        bool websiteStarted = true;
                        if (!string.IsNullOrWhiteSpace(server.PoolName))
                        {
                            appPoolStarted = await manager.StartAppPoolAsync(server);
                            if (!appPoolStarted)
                            {
                                _output.WriteLine($"[WARN] Failed to start app pool '{server.PoolName}' on {server.Name}");
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(server.SiteName))
                        {
                            websiteStarted = await manager.StartWebsiteAsync(server);
                            if (!websiteStarted)
                            {
                                _output.WriteLine($"[WARN] Failed to start website '{server.SiteName}' on {server.Name}");
                            }
                        }
                        if (appPoolStarted && websiteStarted)
                        {
                            await VerifyServerStartedAsync(server, cancellationToken);
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
            await Task.Delay(3000, cancellationToken);
            await _statusService.RefreshServerStatusAsync(server);
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
}
