using System.IO;
using CreatioHelper.Shared.Utils;
using Xunit;

namespace CreatioHelper.UnitTests;

public class CreatioSiteLayoutTests
{
    private static string CreateSite(bool coreConfig, bool webAppDirectory,
        bool nestedDll = false, bool rootDll = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chsl_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        if (coreConfig)
        {
            File.WriteAllText(Path.Combine(dir, "Terrasoft.WebHost.dll.config"), "<configuration />");
        }
        if (webAppDirectory)
        {
            Directory.CreateDirectory(Path.Combine(dir, "Terrasoft.WebApp"));
        }
        if (nestedDll)
        {
            Directory.CreateDirectory(Path.Combine(dir, "Terrasoft.WebApp", "bin"));
            File.WriteAllText(Path.Combine(dir, "Terrasoft.WebApp", "bin", "Terrasoft.Common.dll"), "");
        }
        if (rootDll)
        {
            File.WriteAllText(Path.Combine(dir, "Terrasoft.Common.dll"), "");
        }
        return dir;
    }

    [Fact]
    public void Framework_DetectedByNestedApplicationAssembly()
    {
        var dir = CreateSite(coreConfig: false, webAppDirectory: true, nestedDll: true);

        Assert.True(CreatioSiteLayout.IsDotNetFramework(dir));
        Assert.Equal(Path.Combine(dir, "Terrasoft.WebApp", "bin", "Terrasoft.Common.dll"),
            CreatioSiteLayout.GetApplicationDllPath(dir));
    }

    [Fact]
    public void Core_DetectedByRootApplicationAssembly()
    {
        var dir = CreateSite(coreConfig: false, webAppDirectory: false, rootDll: true);

        Assert.True(CreatioSiteLayout.IsDotNetCore(dir));
        Assert.Equal(Path.Combine(dir, "Terrasoft.Common.dll"), CreatioSiteLayout.GetApplicationDllPath(dir));
    }

    [Fact]
    public void Core_WinsOverLeftoverWebAppFolder_WhenRootAssemblyIsPresent()
    {
        var dir = CreateSite(coreConfig: false, webAppDirectory: true, rootDll: true);

        Assert.True(CreatioSiteLayout.IsDotNetCore(dir));
    }

    [Fact]
    public void CoreConfig_OutweighsNestedAssembly()
    {
        var dir = CreateSite(coreConfig: true, webAppDirectory: true, nestedDll: true);

        Assert.True(CreatioSiteLayout.IsDotNetCore(dir));
    }

    [Fact]
    public void Framework_HasWebAppDirectoryAndNoCoreConfig()
    {
        var dir = CreateSite(coreConfig: false, webAppDirectory: true);

        Assert.True(CreatioSiteLayout.IsDotNetFramework(dir));
        Assert.False(CreatioSiteLayout.IsDotNetCore(dir));
    }

    [Fact]
    public void Core_HasCoreConfigAndNoWebAppDirectory()
    {
        var dir = CreateSite(coreConfig: true, webAppDirectory: false);

        Assert.True(CreatioSiteLayout.IsDotNetCore(dir));
        Assert.False(CreatioSiteLayout.IsDotNetFramework(dir));
    }

    [Fact]
    public void Core_WinsWhenBothMarkersArePresent()
    {
        var dir = CreateSite(coreConfig: true, webAppDirectory: true);

        Assert.True(CreatioSiteLayout.IsDotNetCore(dir));
    }

    [Fact]
    public void Core_IsAssumedWhenNoMarkerIsPresent()
    {
        var dir = CreateSite(coreConfig: false, webAppDirectory: false);

        Assert.True(CreatioSiteLayout.IsDotNetCore(dir));
    }

    [Fact]
    public void GetRootConfigPath_PointsToWebConfig_ForFramework()
    {
        var dir = CreateSite(coreConfig: false, webAppDirectory: true);

        Assert.Equal(Path.Combine(dir, "Web.config"), CreatioSiteLayout.GetRootConfigPath(dir));
    }

    [Fact]
    public void GetRootConfigPath_PointsToWebHostConfig_ForCore()
    {
        var dir = CreateSite(coreConfig: true, webAppDirectory: false);

        Assert.Equal(Path.Combine(dir, "Terrasoft.WebHost.dll.config"), CreatioSiteLayout.GetRootConfigPath(dir));
    }

    [Fact]
    public void GetWebAppPath_AddsNestedApplication_ForFrameworkOnly()
    {
        var framework = CreateSite(coreConfig: false, webAppDirectory: true);
        var core = CreateSite(coreConfig: true, webAppDirectory: false);

        Assert.Equal(Path.Combine(framework, "Terrasoft.WebApp"), CreatioSiteLayout.GetWebAppPath(framework));
        Assert.Equal(core, CreatioSiteLayout.GetWebAppPath(core));
    }

    [Fact]
    public void GetConfigurationPath_FollowsTheWebAppPath()
    {
        var framework = CreateSite(coreConfig: false, webAppDirectory: true);
        var core = CreateSite(coreConfig: true, webAppDirectory: false);

        Assert.Equal(Path.Combine(framework, "Terrasoft.WebApp", "Terrasoft.Configuration"),
            CreatioSiteLayout.GetConfigurationPath(framework));
        Assert.Equal(Path.Combine(core, "Terrasoft.Configuration"),
            CreatioSiteLayout.GetConfigurationPath(core));
    }

    [Fact]
    public void FindExistingRootConfigPath_IgnoresTheAspNetCoreWebConfig()
    {
        var dir = CreateSite(coreConfig: true, webAppDirectory: false, rootDll: true);
        File.WriteAllText(Path.Combine(dir, "Web.config"), "<configuration />");

        Assert.Equal(Path.Combine(dir, "Terrasoft.WebHost.dll.config"), CreatioSiteLayout.FindExistingRootConfigPath(dir));
    }

    [Fact]
    public void FindExistingRootConfigPath_NeverFallsBackAcrossEditions()
    {
        var core = CreateSite(coreConfig: false, webAppDirectory: false, rootDll: true);
        File.WriteAllText(Path.Combine(core, "Web.config"), "<configuration />");

        Assert.Null(CreatioSiteLayout.FindExistingRootConfigPath(core));
    }

    [Fact]
    public void FindExistingRootConfigPath_ReturnsWebConfig_ForFramework()
    {
        var dir = CreateSite(coreConfig: false, webAppDirectory: true, nestedDll: true);
        File.WriteAllText(Path.Combine(dir, "Web.config"), "<configuration />");

        Assert.Equal(Path.Combine(dir, "Web.config"), CreatioSiteLayout.FindExistingRootConfigPath(dir));
    }

    [Fact]
    public void FindExistingRootConfigPath_ReturnsNull_WhenNothingExists()
    {
        var dir = CreateSite(coreConfig: false, webAppDirectory: false);

        Assert.Null(CreatioSiteLayout.FindExistingRootConfigPath(dir));
    }
}
