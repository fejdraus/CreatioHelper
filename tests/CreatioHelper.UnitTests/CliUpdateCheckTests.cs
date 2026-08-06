using CreatioHelper.Cli;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using CreatioHelper.Infrastructure.Services.Updates;
using NuGet.Versioning;
using Xunit;

namespace CreatioHelper.UnitTests;

public class CliUpdateCheckTests
{
    private static CliArgs Args(params string[] args) => CliArgs.Parse(args);

    private static AppSettings Settings(bool updateCheckEnabled = true, UpdateChannel channel = UpdateChannel.Stable)
        => new() { UpdateCheckEnabled = updateCheckEnabled, UpdateChannel = channel };

    [Fact]
    public void CheckIsOnByDefault()
    {
        Assert.True(CliEntryPoint.ShouldCheckForUpdates(Settings(), Args("deploy"), quiet: false));
    }

    [Fact]
    public void FlagTurnsTheCheckOff()
    {
        Assert.False(CliEntryPoint.ShouldCheckForUpdates(Settings(), Args("deploy", "--no-update-check"), quiet: false));
    }

    [Fact]
    public void SettingsTurnTheCheckOff()
    {
        Assert.False(CliEntryPoint.ShouldCheckForUpdates(Settings(updateCheckEnabled: false), Args("deploy"), quiet: false));
    }

    [Fact]
    public void QuietSuppressesTheNotice()
    {
        Assert.False(CliEntryPoint.ShouldCheckForUpdates(Settings(), Args("deploy"), quiet: true));
    }

    [Theory]
    [InlineData("cli-v1.0.34", "1.0.34")]
    [InlineData("cli-v1.0.35-beta.2", "1.0.35-beta.2")]
    [InlineData("CLI-V2.0.0", "2.0.0")]
    public void CliTagsAreParsed(string tag, string expected)
    {
        Assert.True(CliUpdateCheck.TryParseCliVersion(tag, out var version));
        Assert.Equal(NuGetVersion.Parse(expected), version);
    }

    [Theory]
    [InlineData("desktop-v1.0.34")]
    [InlineData("agent-v1.0.34")]
    [InlineData("v1.0.24")]
    [InlineData("cli-vnot-a-version")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherTagsAreIgnored(string? tag)
    {
        Assert.False(CliUpdateCheck.TryParseCliVersion(tag, out _));
    }

    [Fact]
    public void NewerReleaseIsRecognised()
    {
        CliUpdateCheck.TryParseCliVersion("cli-v1.0.35", out var newer);
        CliUpdateCheck.TryParseCliVersion("cli-v1.0.34", out var current);

        Assert.True(newer > current);
    }

    [Fact]
    public void PrereleaseSortsBelowItsRelease()
    {
        CliUpdateCheck.TryParseCliVersion("cli-v1.0.35-beta.1", out var beta);
        CliUpdateCheck.TryParseCliVersion("cli-v1.0.35", out var stable);

        Assert.True(beta < stable);
    }
}
