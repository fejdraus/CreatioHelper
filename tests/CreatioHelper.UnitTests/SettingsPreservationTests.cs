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

    [Fact]
    public void RollingRestartBatchSizeRoundTrips()
    {
        AppSettingsService.Save(new AppSettings { RollingRestartBatchSize = 5 });

        Assert.Equal(5, AppSettingsService.Load().RollingRestartBatchSize);
    }

    [Fact]
    public void RollingRestartBatchSizeSurvivesAnUnrelatedSave()
    {
        AppSettingsService.Save(new AppSettings { RollingRestartBatchSize = 7 });

        var reloaded = AppSettingsService.Load();
        reloaded.PackagesPath = @"C:\packages";
        AppSettingsService.Save(reloaded);

        Assert.Equal(7, AppSettingsService.Load().RollingRestartBatchSize);
    }

    [Fact]
    public void RollingRestartBatchSizeDefaultsToTwoWhenAbsent()
    {
        AppSettingsService.Save(new AppSettings { PackagesPath = @"C:\packages" });

        Assert.Equal(2, AppSettingsService.Load().RollingRestartBatchSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RollingRestartBatchSizeRejectsValuesBelowOne(int value)
    {
        var settings = new AppSettings { RollingRestartBatchSize = value };

        Assert.Equal(1, settings.RollingRestartBatchSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void AHandEditedSettingsFileCannotProduceABatchSizeBelowOne(string stored)
    {
        AppSettingsService.Save(new AppSettings { RollingRestartBatchSize = 4 });
        var path = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.json")[0];
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.Replace("\"RollingRestartBatchSize\": 4", "\"RollingRestartBatchSize\": " + stored));

        Assert.Equal(1, AppSettingsService.Load().RollingRestartBatchSize);
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
