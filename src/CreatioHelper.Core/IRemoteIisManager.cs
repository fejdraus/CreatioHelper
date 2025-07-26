using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Core
{
    public interface IRemoteIisManager
    {
        Task<bool> StopAppPoolAsync(ServerInfo server);
        Task<bool> StopWebsiteAsync(ServerInfo server);
        Task<bool> StartAppPoolAsync(ServerInfo server);
        Task<bool> StartWebsiteAsync(ServerInfo server);
        Task<bool> StartServiceAsync(ServerInfo server);
        Task<bool> StopServiceAsync(ServerInfo server);
        Task GetAppPoolStatusAsync(ServerInfo server);
        Task GetWebsiteStatusAsync(ServerInfo server);
    }
}