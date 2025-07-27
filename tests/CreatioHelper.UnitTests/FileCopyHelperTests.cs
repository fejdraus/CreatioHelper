using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Domain.Entities;
using Moq;

namespace CreatioHelper.Tests;

public class FileCopyHelperTests
{
    [Fact]
    public async Task CopyAsync_OnNonWindows_ThrowsPlatformNotSupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Skip on Windows
        }

        var server = new ServerInfo { Name = "test" };
        var writer = new BufferingOutputWriter(_ => { }, () => { });
        
        // Создаем mock для IMetricsService
        var mockMetrics = new Mock<IMetricsService>();
        
        IFileCopyHelper helper = new RobocopyFileCopyHelper(writer, mockMetrics.Object);
        
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () =>
            await helper.CopyAsync(server, "/tmp/src", "/tmp/dest"));
    }
}
