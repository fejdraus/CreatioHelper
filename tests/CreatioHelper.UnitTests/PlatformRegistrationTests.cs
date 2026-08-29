using System.Runtime.InteropServices;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CreatioHelper.UnitTests;

public class PlatformRegistrationTests
{
    [Fact]
    public void SiteSynchronizer_IsAlwaysResolvable()
    {
        var provider = BuildProvider();

        var synchronizer = provider.GetService<ISiteSynchronizer>();

        Assert.NotNull(synchronizer);
    }

    [Fact]
    public void RemoteIisManager_IsAlwaysResolvable()
    {
        var provider = BuildProvider();

        var manager = provider.GetService<IRemoteIisManager>();

        Assert.NotNull(manager);
    }

    [Fact]
    public void PlatformSpecific_SiteSynchronizer_MatchesCurrentOs()
    {
        var provider = BuildProvider();
        var synchronizer = provider.GetService<ISiteSynchronizer>()!;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(
                "CreatioHelper.Infrastructure.Services.Site.WindowsSiteSynchronizer",
                synchronizer.GetType().FullName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Equal(
                "CreatioHelper.Infrastructure.Services.Linux.LinuxSiteSynchronizer",
                synchronizer.GetType().FullName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Equal(
                "CreatioHelper.Infrastructure.Services.MacOS.MacOsSiteSynchronizer",
                synchronizer.GetType().FullName);
        }
        else
        {

            Assert.NotNull(synchronizer);
        }
    }

    [Fact]
    public void PlatformSpecific_RemoteIisManager_MatchesCurrentOs()
    {
        var provider = BuildProvider();
        var manager = provider.GetService<IRemoteIisManager>()!;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(
                "CreatioHelper.Infrastructure.Services.WindowsRemoteIisManager",
                manager.GetType().FullName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Equal(
                "CreatioHelper.Infrastructure.Services.Linux.LinuxRemoteIisManager",
                manager.GetType().FullName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Equal(
                "CreatioHelper.Infrastructure.Services.MacOs.MacOsRemoteIisManager",
                manager.GetType().FullName);
        }
        else
        {
            Assert.NotNull(manager);
        }
    }

    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddInfrastructureServices(configuration: null);
        return services.BuildServiceProvider();
    }
}