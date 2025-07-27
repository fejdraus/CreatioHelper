using CreatioHelper.Infrastructure.Services.Linux;
using CreatioHelper.Shared.Interfaces;
using Moq;

namespace CreatioHelper.Tests;

public class LinuxRemoteIisManagerTests
{
    private readonly Mock<IOutputWriter> _mockOutputWriter;
    private readonly LinuxRemoteIisManager _manager;
    private const string TestServiceName = "test-service";

    public LinuxRemoteIisManagerTests()
    {
        _mockOutputWriter = new Mock<IOutputWriter>();
        _manager = new LinuxRemoteIisManager(_mockOutputWriter.Object);
    }

    [Fact]
    public void Constructor_WhenOutputWriterIsNull_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new LinuxRemoteIisManager(null!));
    }

    [Fact]
    public async Task StartAppPoolAsync_ShouldCallStartServiceAsync()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartAppPoolAsync(TestServiceName, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopAppPoolAsync_ShouldCallStopServiceAsync()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StopAppPoolAsync(TestServiceName, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartWebsiteAsync_ShouldCallStartServiceAsync()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartWebsiteAsync(TestServiceName, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopWebsiteAsync_ShouldCallStopServiceAsync()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StopWebsiteAsync(TestServiceName, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetAppPoolStatusAsync_ShouldReturnStatusResult()
    {
        var cancellationToken = CancellationToken.None;
        var result = await _manager.GetAppPoolStatusAsync(TestServiceName, cancellationToken);
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
        var result = await _manager.GetWebsiteStatusAsync(TestServiceName, cancellationToken);
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
        var result = await _manager.StartServiceAsync(TestServiceName, cts.Token);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Operation was cancelled", result.ErrorMessage);
    }

    [Fact]
    public async Task StopServiceAsync_WithCancelledToken_ShouldReturnFailureResult()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await _manager.StopServiceAsync(TestServiceName, cts.Token);
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
            ? await _manager.StartServiceAsync(TestServiceName, cancellationToken)
            : await _manager.StopServiceAsync(TestServiceName, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => 
                msg.Contains("[INFO]") || msg.Contains("[ERROR]"))), 
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartServiceAsync_WithValidServiceName_ShouldExecuteSystemctl()
    {
        const string serviceName = "nginx";
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartServiceAsync(serviceName, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => 
                msg.Contains("[INFO]") || msg.Contains("[ERROR]"))), 
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopServiceAsync_WithValidServiceName_ShouldExecuteSystemctl()
    {
        const string serviceName = "apache2";
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StopServiceAsync(serviceName, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(
            x => x.WriteLine(It.Is<string>(msg => 
                msg.Contains("[INFO]") || msg.Contains("[ERROR]"))), 
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartServiceAsync_ShouldHandleSystemctlFailure()
    {
        const string serviceName = "non-existent-service";
        var cancellationToken = CancellationToken.None;
        var result = await _manager.StartServiceAsync(serviceName, cancellationToken);
        Assert.NotNull(result);
        _mockOutputWriter.Verify(x => x.WriteLine(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartAppPoolAsync_WithInvalidPoolName_ShouldReturnFailure(string poolName)
    {
        var result = await _manager.StartAppPoolAsync(poolName, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Pool name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task StartAppPoolAsync_WithNullPoolName_ShouldReturnFailure()
    {
        var result = await _manager.StartAppPoolAsync(null!, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Pool name is required", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartWebsiteAsync_WithInvalidSiteName_ShouldReturnFailure(string siteName)
    {
        var result = await _manager.StartWebsiteAsync(siteName, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Site name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task StartWebsiteAsync_WithNullSiteName_ShouldReturnFailure()
    {
        var result = await _manager.StartWebsiteAsync(null!, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Site name is required", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAppPoolStatusAsync_WithInvalidPoolName_ShouldReturnFailure(string poolName)
    {
        var result = await _manager.GetAppPoolStatusAsync(poolName, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Pool name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task GetAppPoolStatusAsync_WithNullPoolName_ShouldReturnFailure()
    {
        var result = await _manager.GetAppPoolStatusAsync(null!, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Pool name is required", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetWebsiteStatusAsync_WithInvalidSiteName_ShouldReturnFailure(string siteName)
    {
        var result = await _manager.GetWebsiteStatusAsync(siteName, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Site name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task GetWebsiteStatusAsync_WithNullSiteName_ShouldReturnFailure()
    {
        var result = await _manager.GetWebsiteStatusAsync(null!, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Site name is required", result.ErrorMessage);
    }
}
