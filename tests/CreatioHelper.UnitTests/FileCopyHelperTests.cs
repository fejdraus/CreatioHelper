using System;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Domain.Entities;

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
        IFileCopyHelper helper = new RobocopyFileCopyHelper(writer);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () =>
            await helper.CopyAsync(server, "/tmp/src", "/tmp/dest"));
    }
}
