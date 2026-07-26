using CreatioHelper.Application.DTOs;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using Xunit;

namespace CreatioHelper.UnitTests;

/// <summary>
/// Every configuration source maps into SyncFolderSettings and is materialised by
/// SyncFolder.Create, so config.xml and the API DTO must produce equivalent folders.
/// </summary>
public class SyncFolderSettingsTests
{
    [Fact]
    public void ConfigXmlFolder_MinDiskFree_IsFormattedAsValueAndUnit()
    {
        var xml = new ConfigXmlFolder
        {
            Id = "f1",
            Label = "F1",
            Path = "/data",
            MinDiskFree = new ConfigXmlMinDiskFree { Value = 5, Unit = "GB" }
        };

        var folder = SyncFolder.Create(xml.ToSyncFolderSettings());

        Assert.Equal("5GB", folder.MinDiskFree);
    }

    [Fact]
    public void ConfigXmlFolder_PullOrderAndIgnoreDelete_AreApplied()
    {
        var xml = new ConfigXmlFolder
        {
            Id = "f1",
            Label = "F1",
            Path = "/data",
            Order = "oldestfirst",
            IgnoreDelete = true
        };

        var folder = SyncFolder.Create(xml.ToSyncFolderSettings());

        Assert.Equal(SyncPullOrder.OldestFirst, folder.PullOrder);
        Assert.True(folder.IgnoreDelete);
    }

    [Fact]
    public void ConfigXmlFolder_VersioningWithEmptyType_IsNotApplied()
    {
        var xml = new ConfigXmlFolder
        {
            Id = "f1",
            Label = "F1",
            Path = "/data",
            Versioning = new ConfigXmlVersioning { Type = string.Empty }
        };

        var folder = SyncFolder.Create(xml.ToSyncFolderSettings());

        Assert.Null(folder.Versioning);
    }

    [Fact]
    public void ConfigXmlFolder_Versioning_IsMappedWithParams()
    {
        var xml = new ConfigXmlFolder
        {
            Id = "f1",
            Label = "F1",
            Path = "/data",
            Versioning = new ConfigXmlVersioning
            {
                Type = "staggered",
                CleanupIntervalS = 900,
                FsPath = "/versions",
                FsType = "basic",
                Params = new List<ConfigXmlParam> { new() { Key = "maxAge", Val = "30" } }
            }
        };

        var folder = SyncFolder.Create(xml.ToSyncFolderSettings());

        Assert.NotNull(folder.Versioning);
        Assert.Equal("staggered", folder.Versioning!.Type);
        Assert.Equal(900, folder.Versioning.CleanupIntervalS);
        Assert.Equal("30", folder.Versioning.Params["maxAge"]);
    }

    [Fact]
    public void ConfigXmlFolder_Devices_AreAdded()
    {
        var xml = new ConfigXmlFolder
        {
            Id = "f1",
            Label = "F1",
            Path = "/data",
            Devices =
            [
                new ConfigXmlFolderDevice { Id = "DEV-A" },
                new ConfigXmlFolderDevice { Id = "DEV-B" }
            ]
        };

        var folder = SyncFolder.Create(xml.ToSyncFolderSettings());

        Assert.Equal(new[] { "DEV-A", "DEV-B" }, folder.Devices);
    }

    [Fact]
    public void FolderConfiguration_VersioningDisabled_IsNotApplied()
    {
        var config = new FolderConfiguration
        {
            Id = "f1",
            Label = "F1",
            Path = "/data",
            Versioning = new FolderVersioningConfiguration { Type = "none" }
        };

        var folder = SyncFolder.Create(config.ToSyncFolderSettings());

        Assert.Null(folder.Versioning);
    }

    [Fact]
    public void BothSources_WithSameValues_ProduceEquivalentFolders()
    {
        var xml = new ConfigXmlFolder
        {
            Id = "shared",
            Label = "Shared",
            Path = "/data",
            Type = "sendonly",
            RescanIntervalS = 120,
            MaxConflicts = 3,
            MarkerName = ".marker",
            Order = "largestfirst",
            IgnoreDelete = true,
            Paused = true,
            MinDiskFree = new ConfigXmlMinDiskFree { Value = 2, Unit = "%" },
            Devices = [new ConfigXmlFolderDevice { Id = "DEV-A" }]
        };

        var dto = new FolderConfiguration
        {
            Id = "shared",
            Label = "Shared",
            Path = "/data",
            Type = "sendonly",
            RescanIntervalS = 120,
            MaxConflicts = 3,
            MarkerName = ".marker",
            Order = "largestfirst",
            IgnoreDelete = true,
            Paused = true,
            MinDiskFree = new FolderMinDiskFree { Value = 2, Unit = "%" },
            Devices = [new FolderDeviceConfiguration { DeviceId = "DEV-A" }]
        };

        var fromXml = SyncFolder.Create(xml.ToSyncFolderSettings());
        var fromDto = SyncFolder.Create(dto.ToSyncFolderSettings());

        Assert.Equal(fromXml.Type, fromDto.Type);
        Assert.Equal(fromXml.SyncType, fromDto.SyncType);
        Assert.Equal(fromXml.RescanIntervalS, fromDto.RescanIntervalS);
        Assert.Equal(fromXml.MaxConflicts, fromDto.MaxConflicts);
        Assert.Equal(fromXml.MarkerName, fromDto.MarkerName);
        Assert.Equal(fromXml.MinDiskFree, fromDto.MinDiskFree);
        Assert.Equal(fromXml.PullOrder, fromDto.PullOrder);
        Assert.Equal(fromXml.IgnoreDelete, fromDto.IgnoreDelete);
        Assert.Equal(fromXml.IsPaused, fromDto.IsPaused);
        Assert.Equal(fromXml.Devices, fromDto.Devices);
    }

    [Theory]
    [InlineData("alphabetic", SyncPullOrder.Alphabetic)]
    [InlineData("SmallestFirst", SyncPullOrder.SmallestFirst)]
    [InlineData("largestfirst", SyncPullOrder.LargestFirst)]
    [InlineData("oldestfirst", SyncPullOrder.OldestFirst)]
    [InlineData("newestfirst", SyncPullOrder.NewestFirst)]
    [InlineData("random", SyncPullOrder.Random)]
    [InlineData("nonsense", SyncPullOrder.Random)]
    [InlineData(null, SyncPullOrder.Random)]
    public void SyncPullOrders_Parse_IsCaseInsensitiveAndFallsBackToRandom(string? input, SyncPullOrder expected)
    {
        Assert.Equal(expected, SyncPullOrders.Parse(input));
    }
}
