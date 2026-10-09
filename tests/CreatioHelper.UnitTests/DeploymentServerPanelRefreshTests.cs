using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Operations;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests;

public class DeploymentServerPanelRefreshTests
{
    private static ServerInfo[] OneServer() => new[]
    {
        new ServerInfo { Name = "node-1", PoolName = "CreatioPool", SiteName = "Creatio" }
    };

    private static (DeploymentOrchestrator Orchestrator, Mock<IServerStatusService> Status) Build()
    {
        var backups = new Mock<IConfigurationBackupService>();
        backups.Setup(b => b.Read(It.IsAny<string>())).Returns((string path) => new ConfigurationBackup
        {
            Path = path,
            Exists = true,
            ChangedPackages = new[] { new ConfigurationBackupPackage { Name = "UsrPkg", SizeBytes = 1 } }
        });
        backups.Setup(b => b.IsRestoreSupported(It.IsAny<string>())).Returns(true);

        var status = new Mock<IServerStatusService>();
        status.Setup(s => s.RefreshMultipleServerStatusOnUIThreadAsync(It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        status.Setup(s => s.RefreshServerStatusOnUIThreadAsync(It.IsAny<ServerInfo>(), It.IsAny<CancellationToken>()))
            .Returns((ServerInfo s, CancellationToken _) => Task.FromResult(s));

        var orchestrator = new DeploymentOrchestrator(
            Mock.Of<IOutputWriter>(),
            Mock.Of<IIisManager>(),
            Mock.Of<ISiteSynchronizer>(),
            Mock.Of<IWorkspacePreparer>(),
            Mock.Of<ICustomDescriptorUpdater>(),
            Mock.Of<IRedisManagerFactory>(),
            Mock.Of<IMetricsService>(),
            status.Object,
            Mock.Of<IPackageFlagsResetter>(),
            backups.Object);

        return (orchestrator, status);
    }

    [SkippableFact]
    public async Task RunAsync_RefreshesTheServerPanel_WhenItIsVisible()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The server panel is Windows-only.");

        var (orchestrator, status) = Build();
        var servers = OneServer();

        await orchestrator.RunAsync(new DeploymentOptions
        {
            SitePath = Path.Combine(Path.GetTempPath(), "no_such_site_" + Path.GetRandomFileName()),
            IsIisMode = true,
            Compile = CompileMode.None,
            Servers = servers,
            HasRemoteServers = true
        });

        status.Verify(s => s.RefreshMultipleServerStatusOnUIThreadAsync(
            It.Is<ServerInfo[]>(a => a.Length == 1 && a[0].Name == "node-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [SkippableFact]
    public async Task RunAsync_LeavesTheServerPanelAlone_WhenItIsHidden()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The server panel is Windows-only.");

        var (orchestrator, status) = Build();

        await orchestrator.RunAsync(new DeploymentOptions
        {
            SitePath = Path.Combine(Path.GetTempPath(), "no_such_site_" + Path.GetRandomFileName()),
            IsIisMode = true,
            Compile = CompileMode.None,
            Servers = OneServer(),
            HasRemoteServers = false
        });

        status.Verify(s => s.RefreshMultipleServerStatusOnUIThreadAsync(
            It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [SkippableFact]
    public async Task RestoreConfigurationAsync_RefreshesTheServerPanel_WhenItIsVisible()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The server panel is Windows-only.");

        var (orchestrator, status) = Build();

        await orchestrator.RestoreConfigurationAsync(new RestoreConfigurationOptions
        {
            SitePath = Path.Combine(Path.GetTempPath(), "no_such_site_" + Path.GetRandomFileName()),
            IsIisMode = true,
            Compile = CompileMode.None,
            Servers = OneServer(),
            HasRemoteServers = true
        });

        status.Verify(s => s.RefreshMultipleServerStatusOnUIThreadAsync(
            It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
