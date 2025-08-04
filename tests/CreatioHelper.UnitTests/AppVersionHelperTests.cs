using System.Reflection;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Tests;

public class AppVersionHelperTests
{
    [Fact]
    public void GetAppVersion_WhenFileMissing_ReturnsDefaultVersion()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var version = AppVersionHelper.GetAppVersion(tempDir.FullName);
            Assert.Equal(new Version(), version);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void GetAppVersion_WhenValidDllPresent_ReturnsNonDefaultVersion()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var sourceAssembly = Assembly.GetExecutingAssembly().Location;
            var destAssembly = Path.Combine(tempDir.FullName, "Terrasoft.Common.dll");
            File.Copy(sourceAssembly, destAssembly);
            var actual = AppVersionHelper.GetAppVersion(tempDir.FullName);
            Assert.NotNull(actual);
            Assert.NotEqual(new Version(), actual);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }
}