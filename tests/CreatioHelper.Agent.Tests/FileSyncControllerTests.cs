using System;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Contracts.Requests;
using CreatioHelper.Contracts.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Agent.Tests;

public class FileSyncControllerTests
{
    private static FileSyncController CreateController(Mock<IFileSyncService> serviceMock)
    {
        var logger = new Mock<ILogger<FileSyncController>>();
        return new FileSyncController(serviceMock.Object, logger.Object);
    }

    [Fact]
    public async Task ValidatePath_ReturnsIsValid()
    {
        var serviceMock = new Mock<IFileSyncService>();
        serviceMock.Setup(s => s.ValidatePathAsync("path")).ReturnsAsync(true);
        var controller = CreateController(serviceMock);

        var result = await controller.ValidatePath(new ValidatePathRequest { Path = "path" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var value = ok.Value!;
        var valid = (bool)value.GetType().GetProperty("IsValid")!.GetValue(value)!;
        var path = (string)value.GetType().GetProperty("Path")!.GetValue(value)!;
        Assert.True(valid);
        Assert.Equal("path", path);
    }

    [Fact]
    public async Task SyncFiles_ReturnsDtoOnSuccess()
    {
        var serviceMock = new Mock<IFileSyncService>();
        serviceMock.Setup(s => s.SyncAsync("src", "dest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncResult
            {
                Success = true,
                Message = "done",
                BytesTransferred = 10,
                Duration = TimeSpan.Zero
            });
        var controller = CreateController(serviceMock);

        var result = await controller.SyncFiles(new SyncRequest { SourcePath = "src", DestinationPath = "dest" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SyncResult>(ok.Value);
        Assert.True(dto.Success);
        Assert.Equal("done", dto.Message);
        Assert.Equal(10, dto.BytesTransferred);
    }

    [Fact]
    public async Task SyncFiles_ReturnsServerError_OnException()
    {
        var serviceMock = new Mock<IFileSyncService>();
        serviceMock.Setup(s => s.SyncAsync("src", "dest", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fail"));
        var controller = CreateController(serviceMock);

        var result = await controller.SyncFiles(new SyncRequest { SourcePath = "src", DestinationPath = "dest" });

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, obj.StatusCode);
    }

    [Fact]
    public async Task SyncFilesAdvanced_UsesOptions()
    {
        var serviceMock = new Mock<IFileSyncService>();
        serviceMock.Setup(s => s.SyncAsync(It.Is<SyncOptions>(o =>
                o.SourcePath == "src" && o.DestinationPath == "dest" && o.OverwriteExisting),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncResult { Success = true });
        var controller = CreateController(serviceMock);

        var result = await controller.SyncFilesAdvanced(new SyncOptions
        {
            SourcePath = "src",
            DestinationPath = "dest",
            OverwriteExisting = true
        });

        serviceMock.VerifyAll();
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SyncResult>(ok.Value);
        Assert.True(dto.Success);
    }
}
