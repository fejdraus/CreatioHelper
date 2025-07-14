using System.IO;
using CreatioHelper.Core;
using CreatioHelper.Services;

namespace CreatioHelper.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void Load_WhenNoFile_ReturnsDefaultSettings()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir.FullName);
        try
        {
            var service = new SettingsService();
            var settings = service.Load();
            Assert.True(settings.IsIisMode);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Save_And_Load_RoundTrip()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir.FullName);
        try
        {
            var service = new SettingsService();
            var original = new AppSettings
            {
                SitePath = "site",
                PackagesPath = "pkg",
                PackagesToDeleteBefore = "b1",
                PackagesToDeleteAfter = "b2",
                IsIisMode = false,
                IsServerPanelVisible = true
            };
            service.Save(original);

            var loaded = service.Load();
            Assert.Equal(original.SitePath, loaded.SitePath);
            Assert.Equal(original.PackagesPath, loaded.PackagesPath);
            Assert.Equal(original.PackagesToDeleteBefore, loaded.PackagesToDeleteBefore);
            Assert.Equal(original.PackagesToDeleteAfter, loaded.PackagesToDeleteAfter);
            Assert.Equal(original.IsIisMode, loaded.IsIisMode);
            Assert.Equal(original.IsServerPanelVisible, loaded.IsServerPanelVisible);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
            tempDir.Delete(true);
        }
    }
}
