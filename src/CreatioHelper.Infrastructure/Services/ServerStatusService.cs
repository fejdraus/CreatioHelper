using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services
{
    public class ServerStatusService
    {
        private readonly IOutputWriter _output;
        private readonly IRemoteIisManager _remoteIisManager;

        public ServerStatusService(IOutputWriter output, IRemoteIisManager remoteIisManager)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _remoteIisManager = remoteIisManager ?? throw new ArgumentNullException(nameof(remoteIisManager));
        }
        
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
                // Get app pool status
                var poolStatusResult = await _remoteIisManager.GetAppPoolStatusAsync(server.Id, CancellationToken.None);
                if (poolStatusResult.IsSuccess)
                {
                    server.PoolStatus = poolStatusResult.Value ?? "Unknown";
                }
                else
                {
                    server.PoolStatus = "Error";
                    _output.WriteLine($"[ERROR] Failed to get pool status for server '{server.Name}': {poolStatusResult.ErrorMessage}");
                }

                // Get website status
                var siteStatusResult = await _remoteIisManager.GetWebsiteStatusAsync(server.Id, CancellationToken.None);
                if (siteStatusResult.IsSuccess)
                {
                    server.SiteStatus = siteStatusResult.Value ?? "Unknown";
                }
                else
                {
                    server.SiteStatus = "Error";
                    _output.WriteLine($"[ERROR] Failed to get site status for server '{server.Name}': {siteStatusResult.ErrorMessage}");
                }
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