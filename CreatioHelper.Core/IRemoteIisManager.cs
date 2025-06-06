using System.Threading.Tasks;

namespace CreatioHelper.Core
{
    public interface IRemoteIisManager
    {
        Task<bool> StopAppPoolAsync(ServerInfo server);
        Task<bool> StopWebsiteAsync(ServerInfo server);
        Task<bool> StartAppPoolAsync(ServerInfo server);
        Task<bool> StartWebsiteAsync(ServerInfo server);
    }
}