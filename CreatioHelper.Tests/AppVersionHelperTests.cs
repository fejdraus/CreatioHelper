using System;
using System.IO;
using System.Reflection;
using CreatioHelper.Core;

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
    public void GetAppVersion_WhenFileExists_ReturnsAssemblyVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // test is only relevant on Windows
        }
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir.FullName, "Terrasoft.WebApp"));
            var binDir = Path.Combine(tempDir.FullName, "bin");
            Directory.CreateDirectory(binDir);
            var sourceAssembly = Assembly.GetExecutingAssembly().Location;
            var destAssembly = Path.Combine(binDir, "Terrasoft.Common.dll");
            File.Copy(sourceAssembly, destAssembly);

            var expected = AssemblyName.GetAssemblyName(sourceAssembly).Version;
            var actual = AppVersionHelper.GetAppVersion(tempDir.FullName);
            Assert.Equal(expected, actual);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void GetAppVersion_WhenNetEdition_ReturnsAssemblyVersion()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var sourceAssembly = Assembly.GetExecutingAssembly().Location;
            var destAssembly = Path.Combine(tempDir.FullName, "Terrasoft.Common.dll");
            File.Copy(sourceAssembly, destAssembly);

            var expected = AssemblyName.GetAssemblyName(sourceAssembly).Version;
            var actual = AppVersionHelper.GetAppVersion(tempDir.FullName);
            Assert.Equal(expected, actual);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }
}
