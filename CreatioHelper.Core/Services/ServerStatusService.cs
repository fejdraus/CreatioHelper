using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace CreatioHelper.Core.Services
{
    public class ServerStatusService
    {
        private readonly IOutputWriter _output;

        public ServerStatusService(IOutputWriter output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        [SupportedOSPlatform("windows")]
        public async Task RefreshServerStatusAsync(ServerInfo server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));

            // Check the platform before executing
            if (!OperatingSystem.IsWindows())
            {
                server.PoolStatus = "Unsupported";
                server.SiteStatus = "Unsupported";
                _output.WriteLine("[ERROR] Status check is only available on Windows.");
                return;
            }

            server.IsStatusLoading = true;
            server.PoolStatus = "Checking...";
            server.SiteStatus = "Checking...";

            try
            {
                var manager = new RemoteIisManager(_output);
                await manager.GetAppPoolStatusAsync(server);
                await manager.GetWebsiteStatusAsync(server);
            }
            catch (Exception ex)
            {
                server.PoolStatus = "Error";
                server.SiteStatus = "Error";
                _output.WriteLine($"[ERROR] Failed to get status for server '{server.Name}': {ex.Message}");
            }
            finally
            {
                server.IsStatusLoading = false;
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task RefreshMultipleServersStatusAsync(params ServerInfo[] servers)
        {
            var tasks = new Task[servers.Length];
            for (int i = 0; i < servers.Length; i++)
            {
                tasks[i] = RefreshServerStatusAsync(servers[i]);
            }
            
            await Task.WhenAll(tasks);
        }
    }
}