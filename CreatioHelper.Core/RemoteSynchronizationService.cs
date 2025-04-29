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
        private const int MaxConcurrentCopies = 7;
        private static readonly SemaphoreSlim CopySemaphore = new(MaxConcurrentCopies);

        public RemoteSynchronizationService(IOutputWriter output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public async Task<bool> SynchronizeAsync(string siteName, string sitePath, List<ServerInfo> targetServers, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(siteName)) throw new ArgumentNullException(nameof(siteName));
            if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
            if (targetServers == null) throw new ArgumentNullException(nameof(targetServers));

            sitePath = sitePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string currentServer = Environment.MachineName;
            var serversToUpdate = targetServers.Where(s => !s.Name.Equals(currentServer, StringComparison.OrdinalIgnoreCase)).ToList();

            var stopTasks = new List<Task<bool>>();
            foreach (var server in targetServers)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                var manager = new RemoteIisManager(server.Name, _output);
                stopTasks.Add(manager.StopAppPoolAsync(server.PoolName));
                stopTasks.Add(manager.StopWebsiteAsync(server.SiteName));
            }

            bool[] stopResults = await Task.WhenAll(stopTasks);
            if (!stopResults.All(result => result))
            {
                _output.WriteLine("[ERROR] Failed to stop some pools or websites.");
                return false;
            }

            _output.WriteLine("[OK] All pools and websites are stopped.");
            if (cancellationToken.IsCancellationRequested)
                return false;

            string confPath = Path.Combine(sitePath, "Terrasoft.WebApp", "conf");
            string configPath = Path.Combine(sitePath, "Terrasoft.WebApp", "Terrasoft.Configuration");

            var copyTasks = targetServers.Select(server => CopyServerFilesAsync(server, confPath, configPath, cancellationToken)).ToList();
            try
            {
                await Task.WhenAll(copyTasks);
                if (cancellationToken.IsCancellationRequested)
                    return false;

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
                            return;

                        await FileCopyHelper.CopyAsync(server, confPath, destConfPath, _output, cancellationToken);
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        await FileCopyHelper.CopyAsync(server, configPath, destConfigPath, _output, cancellationToken);
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        _output.WriteLine($"[INFO] File copying to {server.Name} completed, starting services...");
                        var manager = new RemoteIisManager(server.Name, _output);

                        bool appPoolStarted = await manager.StartAppPoolAsync(server.PoolName);
                        if (!appPoolStarted)
                            _output.WriteLine($"[WARN] Failed to start app pool on {server.Name}");

                        bool websiteStarted = await manager.StartWebsiteAsync(server.SiteName);
                        if (!websiteStarted)
                            _output.WriteLine($"[WARN] Failed to start website on {server.Name}");

                        _output.WriteLine($"[OK] Synchronization completed for {server.Name}");
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
    }
}