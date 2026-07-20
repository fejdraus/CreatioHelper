using CreatioHelper.Agent.Services;
using Xunit;

namespace CreatioHelper.Agent.Tests;

public class WebSiteRegistryDetectTests
{
    private static IReadOnlyList<(string Name, string PhysicalPath)> Candidates(params (string, string)[] items)
        => items.ToList();

    [Fact]
    public void ResolveOwningSite_FolderUnderNestedApp_ReturnsOwningSite()
    {
        var candidates = Candidates(
            ("Site1", @"R:\testHelper\site1"),
            ("Site1/0", @"R:\testHelper\site1\Terrasoft.WebApp"));

        var site = WebSiteRegistryService.ResolveOwningSite(
            candidates,
            @"R:\testHelper\site1\Terrasoft.WebApp\conf");

        Assert.Equal("Site1", site);
    }

    [Fact]
    public void ResolveOwningSite_NestedCreatioApplication_StripsOnlyWebAppAlias()
    {
        var candidates = Candidates(
            ("Default Web Site", @"C:\inetpub\wwwroot"),
            ("Default Web Site/step1/Step2/SiteInternal", @"C:\inetpub\wwwroot\step1\Step2\SiteInternal"),
            ("Default Web Site/step1/Step2/SiteInternal/0", @"C:\inetpub\wwwroot\step1\Step2\SiteInternal\Terrasoft.WebApp"));

        var site = WebSiteRegistryService.ResolveOwningSite(
            candidates,
            @"C:\inetpub\wwwroot\step1\Step2\SiteInternal\Terrasoft.WebApp\Terrasoft.Configuration");

        Assert.Equal("Default Web Site/step1/Step2/SiteInternal", site);
    }

    [Fact]
    public void ResolveOwningSite_DotNetFrameworkEdition_WithAliasApplication_ReturnsSite()
    {
        var candidates = Candidates(
            ("Site1", @"R:\testHelper\site1"),
            ("Site1/0", @"R:\testHelper\site1\Terrasoft.WebApp"));

        var site = WebSiteRegistryService.ResolveOwningSite(
            candidates,
            @"R:\testHelper\site1\Terrasoft.WebApp\Terrasoft.Configuration");

        Assert.Equal("Site1", site);
    }

    [Fact]
    public void ResolveOwningSite_DotNetCoreEdition_WithoutApplication_ReturnsSite()
    {
        var candidates = Candidates(("HCB", @"P:\Web\HomeCredit"));

        var site = WebSiteRegistryService.ResolveOwningSite(
            candidates,
            @"P:\Web\HomeCredit\Terrasoft.Configuration");

        Assert.Equal("HCB", site);
    }

    [Fact]
    public void ResolveOwningSite_RespectsDirectoryBoundary()
    {
        var candidates = Candidates(("Site1", @"R:\Web\site1"));

        var site = WebSiteRegistryService.ResolveOwningSite(candidates, @"R:\Web\site10\Terrasoft.WebApp");

        Assert.Null(site);
    }

    [Fact]
    public void ResolveOwningSite_PicksCorrectSiteAmongSimilarPrefixes()
    {
        var candidates = Candidates(
            ("Site1", @"R:\Web\site1"),
            ("Site10", @"R:\Web\site10"));

        var site = WebSiteRegistryService.ResolveOwningSite(candidates, @"R:\Web\site10\Terrasoft.WebApp");

        Assert.Equal("Site10", site);
    }

    [Fact]
    public void ResolveOwningSite_NormalizesForwardSlashesInTarget()
    {
        var candidates = Candidates(("Site1", @"R:\testHelper\site1"));

        var site = WebSiteRegistryService.ResolveOwningSite(candidates, "R:/testHelper/site1/conf");

        Assert.Equal("Site1", site);
    }

    [Fact]
    public void ResolveOwningSite_ExactPathMatch_ReturnsSite()
    {
        var candidates = Candidates(("Site1", @"R:\testHelper\site1"));

        var site = WebSiteRegistryService.ResolveOwningSite(candidates, @"R:\testHelper\site1");

        Assert.Equal("Site1", site);
    }

    [Fact]
    public void ResolveOwningSite_NoMatch_ReturnsNull()
    {
        var candidates = Candidates(("Site1", @"R:\testHelper\site1"));

        var site = WebSiteRegistryService.ResolveOwningSite(candidates, @"D:\somewhere\else");

        Assert.Null(site);
    }
}
