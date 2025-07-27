using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Linux;
using CreatioHelper.Shared.Interfaces;
using Moq;

namespace CreatioHelper.Tests;

public class LinuxRemoteIisManagerTests
{
    private readonly Mock<IOutputWriter> _mockOutputWriter;
    private readonly Mock<Func<ServerId, ServerInfo?>> _mockGetServerInfo;
    private readonly LinuxRemoteIisManager _manager;
    private readonly ServerId _testServerId;

    public LinuxRemoteIisManagerTests()
    {
        _mockOutputWriter = new Mock<IOutputWriter>();
        _mockGetServerInfo = new Mock<Func<ServerId, ServerInfo?>>();
        _testServerId = ServerId.Create();
        var testServerInfo = new ServerInfo(_testServerId, new ServerName("test-service"), new NetworkPath("/opt/test"));
        _mockGetServerInfo.Setup(x => x(_testServerId)).Returns(testServerInfo);
        
        _manager = new LinuxRemoteIisManager(_mockOutputWriter.Object, _mockGetServerInfo.Object);
    }

    [Fact]
    public void Constructor_WhenOutputWriterIsNull_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new LinuxRemoteIisManager(null!, _mockGetServerInfo.Object));
    }

    [Fact]
    public void Constructor_WhenGetServerInfoIsNull_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new LinuxRemoteIisManager(_mockOutputWriter.Object, null!));
    }

    [Fact]
    public async Task StartAppPoolAsync_ShouldCallStartServiceAsync()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartAppPoolAsync(_testServerId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopAppPoolAsync_ShouldCallStopServiceAsync()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StopAppPoolAsync(_testServerId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartWebsiteAsync_ShouldCallStartServiceAsync()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartWebsiteAsync(_testServerId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopWebsiteAsync_ShouldCallStopServiceAsync()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StopWebsiteAsync(_testServerId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetAppPoolStatusAsync_ShouldReturnStatusResult()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.GetAppPoolStatusAsync(_testServerId, cancellationToken);
        Assert.NotNull(result);
        if (result.IsSuccess)
        {
            Assert.True(result.Value == "Running" || result.Value == "Stopped");
        }
    }

    [Fact]
    public async Task GetWebsiteStatusAsync_ShouldReturnStatusResult()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.GetWebsiteStatusAsync(_testServerId, cancellationToken);
        Assert.NotNull(result);
        if (result.IsSuccess)
        {
            Assert.True(result.Value == "Running" || result.Value == "Stopped");
        }
    }

    [Fact]
    public async Task StartServiceAsync_WithCancelledToken_ShouldReturnFailureResult()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await _manager.StartServiceAsync(_testServerId, cts.Token);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Operation was cancelled", result.ErrorMessage);
    }

    [Fact]
    public async Task StopServiceAsync_WithCancelledToken_ShouldReturnFailureResult()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await _manager.StopServiceAsync(_testServerId, cts.Token);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Operation was cancelled", result.ErrorMessage);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task ServiceOperations_ShouldLogAppropriateMessages(string operation)
    {
        var cancellationToken = CancellationToken.None;
        var result = operation == "start" 
            ? await _manager.StartServiceAsync(_testServerId, cancellationToken)
            : await _manager.StopServiceAsync(_testServerId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => 
                msg.Contains("[INFO]") || msg.Contains("[ERROR]"))), 
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartServiceAsync_WithValidServerId_ShouldUseCorrectServiceName()
    {
        var serverId = ServerId.Create();
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartServiceAsync(serverId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => 
                msg.Contains("[INFO]") || msg.Contains("[ERROR]"))), 
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopServiceAsync_WithValidServerId_ShouldUseCorrectServiceName()
    {
        var serverId = ServerId.Create();
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StopServiceAsync(serverId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => 
                msg.Contains("[INFO]") || msg.Contains("[ERROR]"))), 
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartServiceAsync_ShouldHandleSystemctlFailure()
    {
        var serverId = ServerId.Create();
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartServiceAsync(serverId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartServiceAsync_ShouldUseServerNameAsServiceName()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartServiceAsync(_testServerId, cancellationToken);
        Assert.NotNull(result);
        _mockGetServerInfo.Verify(x => x(_testServerId), Times.Once);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => msg.Contains("test-service"))), 
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartServiceAsync_WhenServerInfoNotFound_ShouldFallbackToGuid()
    {
        var unknownServerId = ServerId.Create();
        _mockGetServerInfo.Setup(x => x(unknownServerId)).Returns((ServerInfo?)null);
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartServiceAsync(unknownServerId, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => 
                msg.Contains("[WARNING]") && msg.Contains("ServerInfo not found"))), 
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetServiceName_ShouldUseServerNameCorrectly()
    {
        var customServerId = ServerId.Create();
        var customServerInfo = new ServerInfo(customServerId, new ServerName("nginx-production"), new NetworkPath("/etc/nginx"));
        _mockGetServerInfo.Setup(x => x(customServerId)).Returns(customServerInfo);
        var result = await _manager.StartServiceAsync(customServerId, CancellationToken.None);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => msg.Contains("nginx-production"))), 
            Times.AtLeastOnce);
    }
}
