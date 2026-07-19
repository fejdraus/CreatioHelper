using CreatioHelper.Infrastructure.Services.Updates;
using NuGet.Versioning;
using Xunit;

namespace CreatioHelper.UnitTests;

public class UpdateServiceTagTests
{
    [Theory]
    [InlineData("v1.0.24", "1.0.24")]
    [InlineData("desktop-v1.0.25", "1.0.25")]
    [InlineData("cli-v0.4.0", "0.4.0")]
    [InlineData("agent-v2.1.0", "2.1.0")]
    [InlineData("desktop-v1.0.25-beta.3", "1.0.25-beta.3")]
    [InlineData("v1.0.25-beta.3", "1.0.25-beta.3")]
    [InlineData("1.0.24", "1.0.24")]
    public void StripTagPrefix_HandlesLegacyAndPrefixedTags(string tag, string expected)
    {
        Assert.Equal(expected, UpdateService.StripTagPrefix(tag));
    }

    [Theory]
    [InlineData("v1.0.24")]
    [InlineData("desktop-v1.0.25")]
    [InlineData("desktop-v1.0.25-beta.3")]
    public void StrippedTag_IsParsableAsVersion(string tag)
    {
        Assert.True(NuGetVersion.TryParse(UpdateService.StripTagPrefix(tag), out _));
    }
}
