using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Scanning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Agent.Tests;

public class DatabaseControllerTests
{
    private readonly Mock<ISyncEngine> _syncEngineMock;
    private readonly Mock<ILogger<DatabaseController>> _loggerMock;
    private readonly Mock<IScanProgressService> _scanProgressServiceMock;
    private readonly DatabaseController _controller;

    public DatabaseControllerTests()
    {
        _syncEngineMock = new Mock<ISyncEngine>();
        _loggerMock = new Mock<ILogger<DatabaseController>>();
        _scanProgressServiceMock = new Mock<IScanProgressService>();
        _controller = new DatabaseController(_syncEngineMock.Object, _scanProgressServiceMock.Object, _loggerMock.Object);
    }

    #region GetIgnores Tests

    [Fact]
    public async Task GetIgnores_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.GetIgnores(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetIgnores_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);

        var result = await _controller.GetIgnores("unknown");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetIgnores_ReturnsEmptyPatterns_WhenNoStignoreFile()
    {
        var folder = CreateTestFolder("test-folder", Path.GetTempPath());
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);

        var result = await _controller.GetIgnores("test-folder");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region SetIgnores Tests

    [Fact]
    public async Task SetIgnores_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var request = new IgnorePatternsRequest { Ignore = new[] { "*.tmp" } };

        var result = await _controller.SetIgnores(string.Empty, request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task SetIgnores_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);
        var request = new IgnorePatternsRequest { Ignore = new[] { "*.tmp" } };

        var result = await _controller.SetIgnores("unknown", request);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    #endregion

    #region GetRemoteNeed Tests

    [Fact]
    public async Task GetRemoteNeed_ReturnsBadRequest_WhenFolderOrDeviceIsEmpty()
    {
        var result = await _controller.GetRemoteNeed(string.Empty, "device");
        Assert.IsType<BadRequestObjectResult>(result.Result);

        result = await _controller.GetRemoteNeed("folder", string.Empty);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetRemoteNeed_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);

        var result = await _controller.GetRemoteNeed("unknown", "device");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetRemoteNeed_ReturnsEmptyList_WhenFolderExists()
    {
        var folder = CreateTestFolder("test-folder", "/path");
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);
        _syncEngineMock.Setup(s => s.GetNeedListAsync("test-folder", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FolderNeedList());

        var result = await _controller.GetRemoteNeed("test-folder", "device");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region GetLocalChanged Tests

    [Fact]
    public async Task GetLocalChanged_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.GetLocalChanged(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetLocalChanged_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);

        var result = await _controller.GetLocalChanged("unknown");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetLocalChanged_ReturnsEmptyList_WhenFolderExists()
    {
        var folder = CreateTestFolder("test-folder", "/path");
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);
        _syncEngineMock.Setup(s => s.GetNeedListAsync("test-folder", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FolderNeedList());

        var result = await _controller.GetLocalChanged("test-folder");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region SetPriority Tests

    [Fact]
    public async Task SetPriority_ReturnsBadRequest_WhenFolderOrFileIsEmpty()
    {
        var result = await _controller.SetPriority(string.Empty, "file.txt");
        Assert.IsType<BadRequestObjectResult>(result);

        result = await _controller.SetPriority("folder", string.Empty);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetPriority_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);

        var result = await _controller.SetPriority("unknown", "file.txt");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SetPriority_ReturnsOk_WhenFolderExists()
    {
        var folder = CreateTestFolder("test-folder", "/path");
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);

        var result = await _controller.SetPriority("test-folder", "file.txt");

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    #region Existing Endpoints Tests

    [Fact]
    public async Task GetStatus_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.GetStatus(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetCompletion_ReturnsBadRequest_WhenParametersEmpty()
    {
        var result = await _controller.GetCompletion(string.Empty, "device");
        Assert.IsType<BadRequestObjectResult>(result.Result);

        result = await _controller.GetCompletion("folder", string.Empty);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetNeed_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.GetNeed(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task Scan_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.Scan(string.Empty, null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task Override_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.Override(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task Revert_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.Revert(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    #endregion

    private static SyncFolder CreateTestFolder(string id, string path)
    {
        return new SyncFolder(id, id, path);
    }
}
