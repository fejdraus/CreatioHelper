using CreatioHelper.Infrastructure.Services.Updates;
using NuGet.Versioning;
using Xunit;

namespace CreatioHelper.UnitTests;

public class UpdateServiceTagTests
{
    [Theory]
    [InlineData("desktop-v1.0.25", "1.0.25")]
    [InlineData("desktop-v1.0.25-beta.3", "1.0.25-beta.3")]
    [InlineData("Desktop-V2.0.0", "2.0.0")]
    public void TryParseDesktopVersion_AcceptsDesktopTags(string tag, string expected)
    {
        Assert.True(UpdateService.TryParseDesktopVersion(tag, out var version));
        Assert.Equal(NuGetVersion.Parse(expected), version);
    }

    [Theory]
    [InlineData("v1.0.24")]
    [InlineData("v1.0.25-beta.3")]
    [InlineData("cli-v0.4.0")]
    [InlineData("agent-v2.1.0")]
    [InlineData("1.0.24")]
    [InlineData("")]
    [InlineData("desktop-1.0.25")]
    public void TryParseDesktopVersion_RejectsNonDesktopTags(string tag)
    {
        Assert.False(UpdateService.TryParseDesktopVersion(tag, out _));
    }
}
