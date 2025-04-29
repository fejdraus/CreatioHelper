using System.Threading.Tasks;

namespace CreatioHelper.Core
{
    public interface IRemoteIisManager
    {
        Task<bool> StopAppPoolAsync(string poolName);
        Task<bool> StopWebsiteAsync(string siteName);
        Task<bool> StartAppPoolAsync(string poolName);
        Task<bool> StartWebsiteAsync(string siteName);
    }
}