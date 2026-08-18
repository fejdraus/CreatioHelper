using System;
using System.IO;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using CreatioHelper.Infrastructure.Services;
using Xunit;

namespace CreatioHelper.UnitTests;

[Collection(CreatioHelper.Tests.CurrentDirectoryCollection.Name)]
public class SettingsPreservationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _originalDirectory;

    public SettingsPreservationTests()
    {
        _originalDirectory = Directory.GetCurrentDirectory();
        _directory = Path.Combine(Path.GetTempPath(), $"chsettings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        Directory.SetCurrentDirectory(_directory);
    }

    [Fact]
    public void UpdatePreferencesSurviveASaveThatDoesNotMentionThem()
    {
        var stored = new AppSettings
        {
            UpdateCheckEnabled = false,
            UpdateChannel = UpdateChannel.Beta
        };
        AppSettingsService.Save(stored);

        var reloaded = AppSettingsService.Load();
        reloaded.PackagesPath = @"C:\packages";
        AppSettingsService.Save(reloaded);

        var result = AppSettingsService.Load();

        Assert.False(result.UpdateCheckEnabled);
        Assert.Equal(UpdateChannel.Beta, result.UpdateChannel);
        Assert.Equal(@"C:\packages", result.PackagesPath);
    }

    [Fact]
    public void BuildingSettingsFromScratchLosesWhatItDoesNotList()
    {
        AppSettingsService.Save(new AppSettings { UpdateCheckEnabled = false });

        AppSettingsService.Save(new AppSettings { PackagesPath = @"C:\packages" });

        Assert.True(AppSettingsService.Load().UpdateCheckEnabled);
    }

    [Fact]
    public void DisabledCheckRoundTrips()
    {
        AppSettingsService.Save(new AppSettings { UpdateCheckEnabled = false });

        Assert.False(AppSettingsService.Load().UpdateCheckEnabled);
    }

    [Fact]
    public void ChannelRoundTrips()
    {
        AppSettingsService.Save(new AppSettings { UpdateChannel = UpdateChannel.Beta });

        Assert.Equal(UpdateChannel.Beta, AppSettingsService.Load().UpdateChannel);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDirectory);
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
