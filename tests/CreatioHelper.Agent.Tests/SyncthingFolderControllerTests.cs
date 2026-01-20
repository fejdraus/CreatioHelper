using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Agent.Tests;

public class SyncthingFolderControllerTests
{
    private readonly Mock<ISyncEngine> _syncEngineMock;
    private readonly Mock<IVersionerFactory> _versionerFactoryMock;
    private readonly Mock<ILogger<SyncthingFolderController>> _loggerMock;
    private readonly SyncthingFolderController _controller;

    public SyncthingFolderControllerTests()
    {
        _syncEngineMock = new Mock<ISyncEngine>();
        _versionerFactoryMock = new Mock<IVersionerFactory>();
        _loggerMock = new Mock<ILogger<SyncthingFolderController>>();
        _controller = new SyncthingFolderController(
            _syncEngineMock.Object,
            _versionerFactoryMock.Object,
            _loggerMock.Object);
    }

    #region GetVersions Tests

    [Fact]
    public async Task GetVersions_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.GetVersions(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetVersions_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);

        var result = await _controller.GetVersions("unknown");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetVersions_ReturnsEmptyDictionary_WhenVersioningDisabled()
    {
        var folder = CreateTestFolder("test-folder", "/path");
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);

        var result = await _controller.GetVersions("test-folder");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var versions = Assert.IsType<Dictionary<string, object[]>>(ok.Value);
        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetVersions_ReturnsVersions_WhenVersioningEnabled()
    {
        var folder = CreateTestFolderWithVersioning("test-folder", "/path");
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);

        var versionerMock = new Mock<IVersioner>();
        versionerMock.Setup(v => v.GetVersionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, List<FileVersion>>
            {
                ["file.txt"] = new List<FileVersion>
                {
                    new FileVersion
                    {
                        VersionTime = DateTime.UtcNow,
                        ModTime = DateTime.UtcNow,
                        Size = 100
                    }
                }
            });
        _versionerFactoryMock.Setup(f => f.CreateVersioner(It.IsAny<string>(), It.IsAny<VersioningConfiguration>()))
            .Returns(versionerMock.Object);

        var result = await _controller.GetVersions("test-folder");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region RestoreVersions Tests

    [Fact]
    public async Task RestoreVersions_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var request = new Dictionary<string, string> { ["file.txt"] = "2024-01-01T00:00:00Z" };

        var result = await _controller.RestoreVersions(string.Empty, request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task RestoreVersions_ReturnsBadRequest_WhenRequestIsEmpty()
    {
        var result = await _controller.RestoreVersions("test-folder", new Dictionary<string, string>());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("restore request body required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task RestoreVersions_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);
        var request = new Dictionary<string, string> { ["file.txt"] = "2024-01-01T00:00:00Z" };

        var result = await _controller.RestoreVersions("unknown", request);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task RestoreVersions_ReturnsBadRequest_WhenVersioningDisabled()
    {
        var folder = CreateTestFolder("test-folder", "/path");
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);
        var request = new Dictionary<string, string> { ["file.txt"] = "2024-01-01T00:00:00Z" };

        var result = await _controller.RestoreVersions("test-folder", request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    #endregion

    #region GetErrors Tests

    [Fact]
    public async Task GetErrors_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.GetErrors(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetErrors_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);

        var result = await _controller.GetErrors("unknown");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetErrors_ReturnsErrors_WhenFolderExists()
    {
        var folder = CreateTestFolder("test-folder", "/path");
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);
        _syncEngineMock.Setup(s => s.GetSyncStatusAsync("test-folder"))
            .ReturnsAsync(new SyncStatus { FolderId = "test-folder", Errors = new List<string>() });

        var result = await _controller.GetErrors("test-folder");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region GetStatus Tests

    [Fact]
    public async Task GetStatus_ReturnsBadRequest_WhenFolderIsEmpty()
    {
        var result = await _controller.GetStatus(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("folder parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetStatus_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);

        var result = await _controller.GetStatus("unknown");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetStatus_ReturnsStatus_WhenFolderExists()
    {
        var folder = CreateTestFolder("test-folder", "/path");
        _syncEngineMock.Setup(s => s.GetFolderAsync("test-folder"))
            .ReturnsAsync(folder);
        _syncEngineMock.Setup(s => s.GetSyncStatusAsync("test-folder"))
            .ReturnsAsync(new SyncStatus
            {
                FolderId = "test-folder",
                State = SyncState.Idle,
                TotalFiles = 10,
                LocalFiles = 10,
                Errors = new List<string>()
            });

        var result = await _controller.GetStatus("test-folder");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    private static SyncFolder CreateTestFolder(string id, string path)
    {
        return new SyncFolder(id, id, path);
    }

    private static SyncFolder CreateTestFolderWithVersioning(string id, string path)
    {
        var folder = new SyncFolder(id, id, path);
        folder.SetVersioning(VersioningConfiguration.Simple(keep: 5));
        return folder;
    }
}
