using System;
using System.Threading.Tasks;
using CreatioHelper.Core;

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
        var writer = new BufferingOutputWriter(_ => { });
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () =>
            await FileCopyHelper.CopyAsync(server, "/tmp/src", "/tmp/dest", writer));
    }
}
