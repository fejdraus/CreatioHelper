using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Common;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Application.Interfaces;
using Moq;
using Xunit.Abstractions;

namespace CreatioHelper.Tests;

public class WindowsSiteSynchronizerTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public WindowsSiteSynchronizerTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task SynchronizeAsync_ThrowsArgumentNullException_When_SiteInfoIsNull()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        var iisManager = new Mock<IIisManager>();
        var copy = new Mock<IFileCopyHelper>();
        var status = new Mock<IServerStatusService>();
        // Mock successful status refresh - sets server properties to "Stopped" then "Started"
        status.Setup(s => s.RefreshMultipleServerStatusAsync(It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo[], CancellationToken>((servers, ct) =>
              {
                  foreach (var server in servers)
                  {
                      server.PoolStatus = "Stopped";
                      server.SiteStatus = "Stopped";
                  }
              })
              .Returns(Task.CompletedTask);
        status.Setup(s => s.RefreshServerStatusAsync(It.IsAny<ServerInfo>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo, CancellationToken>((server, ct) =>
              {
                  server.PoolStatus = "Started";
                  server.SiteStatus = "Started";
              })
              .Returns(Task.FromResult(It.IsAny<ServerInfo>()));
        var sync = new WindowsSiteSynchronizer(writer, iisManager.Object, copy.Object, status.Object);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.SynchronizeAsync(null!, new List<ServerInfo>()));
    }

    [Fact]
    public async Task SynchronizeAsync_ReturnsFalse_When_StopFails()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var iisManager = new Mock<IIisManager>();
        var remote = new Mock<IRemoteIisManager>();
        
        // Configure mocks for new IIisManager methods with serverName parameter
        iisManager.Setup(r => r.StopAppPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Failure("Stop failed"));
        iisManager.Setup(r => r.StopWebsiteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());

        var copy = new Mock<IFileCopyHelper>();
        var status = new Mock<IServerStatusService>();
        // Mock successful status refresh - sets server properties to "Stopped" then "Started"
        status.Setup(s => s.RefreshMultipleServerStatusAsync(It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo[], CancellationToken>((servers, ct) =>
              {
                  foreach (var server in servers)
                  {
                      server.PoolStatus = "Stopped";
                      server.SiteStatus = "Stopped";
                  }
              })
              .Returns(Task.CompletedTask);
        status.Setup(s => s.RefreshServerStatusAsync(It.IsAny<ServerInfo>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo, CancellationToken>((server, ct) =>
              {
                  server.PoolStatus = "Started";
                  server.SiteStatus = "Started";
              })
              .Returns(Task.FromResult(It.IsAny<ServerInfo>()));
        var sync = new WindowsSiteSynchronizer(writer, iisManager.Object, copy.Object, status.Object);

        var servers = new List<ServerInfo>
        {
            new() { Name = "TestServer", PoolName = "TestPool", SiteName = "TestSite" }
        };

        var result = await sync.SynchronizeAsync("/test/path", servers);

        Assert.False(result);
    }

    [Fact]
    public async Task SynchronizeAsync_ReturnsFalse_When_FileCopyFails()
    {
        var output = new List<string>();
        var writer = new BufferingOutputWriter(line => output.Add(line));
        var iisManager = new Mock<IIisManager>();
        
        // Configure successful stop operations
        iisManager.Setup(r => r.StopAppPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        iisManager.Setup(r => r.StopWebsiteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());

        var copy = new Mock<IFileCopyHelper>();
        copy.Setup(c => c.CopyAsync(It.IsAny<ServerInfo>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("File copy failed")); // File copy fails

        var status = new Mock<IServerStatusService>();
        // Mock successful status refresh - sets server properties to "Stopped"
        status.Setup(s => s.RefreshMultipleServerStatusAsync(It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo[], CancellationToken>((servers, ct) =>
              {
                  foreach (var server in servers)
                  {
                      server.PoolStatus = "Stopped";
                      server.SiteStatus = "Stopped";
                  }
              })
              .Returns(Task.CompletedTask);
        
        var sync = new WindowsSiteSynchronizer(writer, iisManager.Object, copy.Object, status.Object);

        var servers = new List<ServerInfo>
        {
            new() { Name = "TestServer", PoolName = "TestPool", SiteName = "TestSite", NetworkPath = "/test/network" }
        };

        var result = await sync.SynchronizeAsync("/test/path", servers);

        // For debugging - check what output was produced
        _testOutputHelper.WriteLine(string.Join("\n", output));
        
        Assert.False(result);
    }

    [Fact]
    public async Task SynchronizeAsync_ReturnsTrue_When_AllOperationsSucceed()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var iisManager = new Mock<IIisManager>();
        var remote = new Mock<IRemoteIisManager>();
        
        // Configure successful operations
        iisManager.Setup(r => r.StopAppPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        iisManager.Setup(r => r.StopWebsiteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        iisManager.Setup(r => r.StartAppPoolAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        iisManager.Setup(r => r.StartWebsiteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());

        var copy = new Mock<IFileCopyHelper>();
        copy.Setup(c => c.CopyAsync(It.IsAny<ServerInfo>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(0)); // File copy succeeds

        var status = new Mock<IServerStatusService>();
        // Mock successful status refresh - sets server properties to "Stopped" then "Started"
        status.Setup(s => s.RefreshMultipleServerStatusAsync(It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo[], CancellationToken>((servers, ct) =>
              {
                  foreach (var server in servers)
                  {
                      server.PoolStatus = "Stopped";
                      server.SiteStatus = "Stopped";
                  }
              })
              .Returns(Task.CompletedTask);
        status.Setup(s => s.RefreshServerStatusAsync(It.IsAny<ServerInfo>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo, CancellationToken>((server, ct) =>
              {
                  server.PoolStatus = "Started";
                  server.SiteStatus = "Started";
              })
              .Returns(Task.FromResult(It.IsAny<ServerInfo>()));
        var sync = new WindowsSiteSynchronizer(writer, iisManager.Object, copy.Object, status.Object);

        var servers = new List<ServerInfo>
        {
            new() { Name = "TestServer", PoolName = "TestPool", SiteName = "TestSite" }
        };

        var result = await sync.SynchronizeAsync("/test/path", servers);

        Assert.True(result);
    }

    [Fact]
    public async Task SynchronizeAsync_HandlesEmptyServerList()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var iisManager = new Mock<IIisManager>();
        var remote = new Mock<IRemoteIisManager>();
        var copy = new Mock<IFileCopyHelper>();
        var status = new Mock<IServerStatusService>();
        // Mock successful status refresh - sets server properties to "Stopped" then "Started"
        status.Setup(s => s.RefreshMultipleServerStatusAsync(It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo[], CancellationToken>((servers, ct) =>
              {
                  foreach (var server in servers)
                  {
                      server.PoolStatus = "Stopped";
                      server.SiteStatus = "Stopped";
                  }
              })
              .Returns(Task.CompletedTask);
        status.Setup(s => s.RefreshServerStatusAsync(It.IsAny<ServerInfo>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo, CancellationToken>((server, ct) =>
              {
                  server.PoolStatus = "Started";
                  server.SiteStatus = "Started";
              })
              .Returns(Task.FromResult(It.IsAny<ServerInfo>()));
        var sync = new WindowsSiteSynchronizer(writer, iisManager.Object, copy.Object, status.Object);

        var result = await sync.SynchronizeAsync("/test/path", new List<ServerInfo>());

        Assert.True(result); // Should succeed with empty server list
    }

    [Fact]
    public async Task SynchronizeAsync_HandlesServerWithoutPoolOrSite()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var iisManager = new Mock<IIisManager>();
        var remote = new Mock<IRemoteIisManager>();
        var copy = new Mock<IFileCopyHelper>();
        var status = new Mock<IServerStatusService>();
        // Mock successful status refresh - sets server properties to "Stopped" then "Started"
        status.Setup(s => s.RefreshMultipleServerStatusAsync(It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo[], CancellationToken>((servers, ct) =>
              {
                  foreach (var server in servers)
                  {
                      server.PoolStatus = "Stopped";
                      server.SiteStatus = "Stopped";
                  }
              })
              .Returns(Task.CompletedTask);
        status.Setup(s => s.RefreshServerStatusAsync(It.IsAny<ServerInfo>(), It.IsAny<CancellationToken>()))
              .Callback<ServerInfo, CancellationToken>((server, ct) =>
              {
                  server.PoolStatus = "Started";
                  server.SiteStatus = "Started";
              })
              .Returns(Task.FromResult(It.IsAny<ServerInfo>()));
        var sync = new WindowsSiteSynchronizer(writer, iisManager.Object, copy.Object, status.Object);

        var servers = new List<ServerInfo>
        {
            new() { Name = "TestServer" } // No PoolName or SiteName
        };

        var result = await sync.SynchronizeAsync("/test/path", servers);

        Assert.True(result); // Should succeed even without pool/site names
    }
}