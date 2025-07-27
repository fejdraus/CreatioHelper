using System.IO;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;

namespace CreatioHelper.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly List<DirectoryInfo> _tempDirectories = new();

    [Fact]
    public void Load_WhenNoFile_ReturnsDefaultSettings()
    {
        var tempDir = CreateTempDirectory();
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir.FullName);
        try
        {
            var manager = new AppSettingsManager();
            var service = new SettingsService(manager);
            var settings = service.Load();
            Assert.True(settings.IsIisMode);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Fact]
    public void Save_And_Load_RoundTrip()
    {
        var tempDir = CreateTempDirectory();
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir.FullName);
        try
        {
            var manager = new AppSettingsManager();
            var service = new SettingsService(manager);
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
        }
    }

    private DirectoryInfo CreateTempDirectory()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    public void Dispose()
    {
        foreach (var tempDir in _tempDirectories)
        {
            if (tempDir.Exists)
            {
                TryDeleteDirectory(tempDir);
            }
        }
    }

    private static void TryDeleteDirectory(DirectoryInfo directory)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                SetDirectoryAttributesRecursive(directory, FileAttributes.Normal);
                directory.Delete(true);
                break;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (i < 4)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void SetDirectoryAttributesRecursive(DirectoryInfo directory, FileAttributes attributes)
    {
        if (!directory.Exists)
            return;

        foreach (var file in directory.EnumerateFiles())
        {
            if (file.Exists && (file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                file.Attributes = attributes;
            }
        }

        foreach (var subDir in directory.EnumerateDirectories())
        {
            SetDirectoryAttributesRecursive(subDir, attributes);
        }

        if ((directory.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
        {
            directory.Attributes = attributes;
        }
    }
}
