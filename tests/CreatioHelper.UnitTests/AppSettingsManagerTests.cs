using System.IO;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Configuration;

namespace CreatioHelper.Tests;

public class AppSettingsManagerTests
{
    [Fact]
    public void Load_WhenNoFile_ReturnsDefaultSettings()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir.FullName);
        try
        {
            var manager = new AppSettingsManager();
            var settings = manager.Load();
            Assert.True(settings.IsIisMode);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    tempDir.Delete(true);
                    break;
                }
                catch (IOException) when (i < 2)
                {
                    Thread.Sleep(100);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
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
            var manager = new AppSettingsManager();
            var original = new AppSettings
            {
                SitePath = "site",
                PackagesPath = "pkg",
                PackagesToDeleteBefore = "b1",
                PackagesToDeleteAfter = "b2",
                IsIisMode = false,
                IsServerPanelVisible = true
            };
            manager.Save(original);

            var loaded = manager.Load();
            Assert.Equal(original.SitePath, loaded.SitePath);
            Assert.Equal(original.PackagesPath, loaded.PackagesPath);
            Assert.Equal(original.PackagesToDeleteBefore, loaded.PackagesToDeleteBefore);
            Assert.Equal(original.PackagesToDeleteAfter, loaded.PackagesToDeleteAfter);
            Assert.Equal(original.IsIisMode, loaded.IsIisMode);
            Assert.Equal(original.IsServerPanelVisible, loaded.IsServerPanelVisible);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    tempDir.Delete(true);
                    break;
                }
                catch (IOException) when (i < 2)
                {
                    // Ждем немного и пытаемся снова
                    Thread.Sleep(100);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }
    }
}
