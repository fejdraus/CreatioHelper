using System.Threading.Tasks;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Linux;

public class LinuxRemoteIisManager : IRemoteIisManager
{
    public Task<bool> StopAppPoolAsync(ServerInfo server)
    {
        return Task.FromResult(false);
    }

    public Task<bool> StopWebsiteAsync(ServerInfo server)
    {
        return Task.FromResult(false);
    }

    public Task<bool> StartAppPoolAsync(ServerInfo server)
    {
        return Task.FromResult(false);
    }

    public Task<bool> StartWebsiteAsync(ServerInfo server)
    {
        return Task.FromResult(false);
    }

    public Task<bool> StartServiceAsync(ServerInfo server)
    {
        return Task.FromResult(false);
    }

    public Task<bool> StopServiceAsync(ServerInfo server)
    {
        return Task.FromResult(false);
    }

    public Task GetAppPoolStatusAsync(ServerInfo server)
    {
        return Task.CompletedTask;
    }

    public Task GetWebsiteStatusAsync(ServerInfo server)
    {
        return Task.CompletedTask;
    }
}
