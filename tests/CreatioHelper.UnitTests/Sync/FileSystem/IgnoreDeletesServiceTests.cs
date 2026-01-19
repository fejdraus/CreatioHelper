using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.FileSystem;

public class IgnoreDeletesServiceTests
{
    private readonly Mock<ILogger<IgnoreDeletesService>> _loggerMock;
    private readonly IgnoreDeletesConfiguration _config;
    private readonly IgnoreDeletesService _service;

    public IgnoreDeletesServiceTests()
    {
        _loggerMock = new Mock<ILogger<IgnoreDeletesService>>();
        _config = new IgnoreDeletesConfiguration();
        _service = new IgnoreDeletesService(_loggerMock.Object, _config);
    }

    #region ShouldIgnoreDeletes Tests

    [Fact]
    public void ShouldIgnoreDeletes_Default_ReturnsFalse()
    {
        Assert.False(_service.ShouldIgnoreDeletes("folder1"));
    }

    [Fact]
    public void ShouldIgnoreDeletes_AfterSet_ReturnsTrue()
    {
        _service.SetIgnoreDeletes("folder1", true);

        Assert.True(_service.ShouldIgnoreDeletes("folder1"));
    }

    [Fact]
    public void ShouldIgnoreDeletes_ConfigDefault_UsesDefault()
    {
        var config = new IgnoreDeletesConfiguration { DefaultIgnoreDeletes = true };
        var service = new IgnoreDeletesService(_loggerMock.Object, config);

        Assert.True(service.ShouldIgnoreDeletes("folder1"));
    }

    [Fact]
    public void ShouldIgnoreDeletes_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldIgnoreDeletes(null!));
    }

    #endregion

    #region ShouldIgnoreDeletesFrom Tests

    [Fact]
    public void ShouldIgnoreDeletesFrom_NoDeviceSetting_FallsBackToFolder()
    {
        _service.SetIgnoreDeletes("folder1", true);

        Assert.True(_service.ShouldIgnoreDeletesFrom("folder1", "device1"));
    }

    [Fact]
    public void ShouldIgnoreDeletesFrom_DeviceSettingOverrides()
    {
        _service.SetIgnoreDeletes("folder1", false);
        _service.SetIgnoreDeletesFromDevice("folder1", "device1", true);

        Assert.True(_service.ShouldIgnoreDeletesFrom("folder1", "device1"));
        Assert.False(_service.ShouldIgnoreDeletesFrom("folder1", "device2"));
    }

    [Fact]
    public void ShouldIgnoreDeletesFrom_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldIgnoreDeletesFrom(null!, "device1"));
    }

    [Fact]
    public void ShouldIgnoreDeletesFrom_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldIgnoreDeletesFrom("folder1", null!));
    }

    #endregion

    #region SetIgnoreDeletes Tests

    [Fact]
    public void SetIgnoreDeletes_True_SetsCorrectly()
    {
        _service.SetIgnoreDeletes("folder1", true);

        Assert.True(_service.ShouldIgnoreDeletes("folder1"));
    }

    [Fact]
    public void SetIgnoreDeletes_False_SetsCorrectly()
    {
        _service.SetIgnoreDeletes("folder1", true);
        _service.SetIgnoreDeletes("folder1", false);

        Assert.False(_service.ShouldIgnoreDeletes("folder1"));
    }

    [Fact]
    public void SetIgnoreDeletes_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetIgnoreDeletes(null!, true));
    }

    #endregion

    #region GetFoldersWithIgnoreDeletes Tests

    [Fact]
    public void GetFoldersWithIgnoreDeletes_NoneEnabled_ReturnsEmpty()
    {
        var folders = _service.GetFoldersWithIgnoreDeletes();

        Assert.Empty(folders);
    }

    [Fact]
    public void GetFoldersWithIgnoreDeletes_SomeEnabled_ReturnsEnabled()
    {
        _service.SetIgnoreDeletes("folder1", true);
        _service.SetIgnoreDeletes("folder2", false);
        _service.SetIgnoreDeletes("folder3", true);

        var folders = _service.GetFoldersWithIgnoreDeletes();

        Assert.Equal(2, folders.Count);
        Assert.Contains("folder1", folders);
        Assert.Contains("folder3", folders);
    }

    #endregion

    #region ShouldApplyDelete Tests

    [Fact]
    public void ShouldApplyDelete_NotIgnoring_ReturnsApply()
    {
        var decision = _service.ShouldApplyDelete("folder1", "device1", "/path/file.txt");

        Assert.Equal(DeleteDecision.Apply, decision);
    }

    [Fact]
    public void ShouldApplyDelete_FolderIgnoring_ReturnsIgnoreFolder()
    {
        _service.SetIgnoreDeletes("folder1", true);

        var decision = _service.ShouldApplyDelete("folder1", "device1", "/path/file.txt");

        Assert.Equal(DeleteDecision.IgnoreFolder, decision);
    }

    [Fact]
    public void ShouldApplyDelete_DeviceIgnoring_ReturnsIgnoreDevice()
    {
        _service.SetIgnoreDeletesFromDevice("folder1", "device1", true);

        var decision = _service.ShouldApplyDelete("folder1", "device1", "/path/file.txt");

        Assert.Equal(DeleteDecision.IgnoreDevice, decision);
    }

    [Fact]
    public void ShouldApplyDelete_PatternMatch_ReturnsIgnorePattern()
    {
        _config.IgnoreDeletePatterns.Add("*.bak");

        var decision = _service.ShouldApplyDelete("folder1", "device1", "/path/file.bak");

        Assert.Equal(DeleteDecision.IgnorePattern, decision);
    }

    [Fact]
    public void ShouldApplyDelete_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldApplyDelete(null!, "device1", "/path"));
    }

    [Fact]
    public void ShouldApplyDelete_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldApplyDelete("folder1", null!, "/path"));
    }

    [Fact]
    public void ShouldApplyDelete_NullFilePath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldApplyDelete("folder1", "device1", null!));
    }

    #endregion

    #region RecordSkippedDelete Tests

    [Fact]
    public void RecordSkippedDelete_UpdatesStats()
    {
        _service.SetIgnoreDeletes("folder1", true);
        _service.RecordSkippedDelete("folder1", "device1", "/path/file.txt");

        var stats = _service.GetStats("folder1");

        Assert.Equal(1, stats.DeletesSkipped);
    }

    [Fact]
    public void RecordSkippedDelete_TracksInHistory()
    {
        _service.SetIgnoreDeletes("folder1", true);
        _service.RecordSkippedDelete("folder1", "device1", "/path/file.txt");

        var stats = _service.GetStats("folder1");

        Assert.Single(stats.RecentSkippedDeletes);
        Assert.Equal("/path/file.txt", stats.RecentSkippedDeletes[0].FilePath);
    }

    [Fact]
    public void RecordSkippedDelete_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.RecordSkippedDelete(null!, "device1", "/path"));
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_Initial_ReturnsEmptyStats()
    {
        var stats = _service.GetStats("folder1");

        Assert.Equal("folder1", stats.FolderId);
        Assert.Equal(0, stats.TotalDeletesReceived);
        Assert.Equal(0, stats.DeletesApplied);
        Assert.Equal(0, stats.DeletesSkipped);
    }

    [Fact]
    public void GetStats_AfterOperations_TracksCorrectly()
    {
        _service.ShouldApplyDelete("folder1", "device1", "/file1.txt"); // Apply
        _service.SetIgnoreDeletes("folder1", true);
        _service.ShouldApplyDelete("folder1", "device1", "/file2.txt"); // Ignore

        var stats = _service.GetStats("folder1");

        Assert.Equal(2, stats.TotalDeletesReceived);
        Assert.Equal(1, stats.DeletesApplied);
    }

    [Fact]
    public void GetStats_SkipRate_CalculatesCorrectly()
    {
        // RecordSkippedDelete internally calls ShouldApplyDelete, so:
        // - ShouldApplyDelete("/file1.txt") -> TotalDeletesReceived = 1, DeletesApplied = 1
        // - SetIgnoreDeletes(true)
        // - RecordSkippedDelete("/file2.txt") -> ShouldApplyDelete internally -> TotalDeletesReceived = 2, DeletesSkipped = 1
        // SkipRate = 1/2 * 100 = 50%
        _service.ShouldApplyDelete("folder1", "device1", "/file1.txt");
        _service.SetIgnoreDeletes("folder1", true);
        _service.RecordSkippedDelete("folder1", "device1", "/file2.txt");

        var stats = _service.GetStats("folder1");

        Assert.Equal(50.0, stats.SkipRate);
    }

    [Fact]
    public void GetStats_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStats(null!));
    }

    #endregion

    #region ClearSkippedDeletes Tests

    [Fact]
    public void ClearSkippedDeletes_ClearsHistory()
    {
        _service.SetIgnoreDeletes("folder1", true);
        _service.RecordSkippedDelete("folder1", "device1", "/file1.txt");
        _service.RecordSkippedDelete("folder1", "device1", "/file2.txt");

        _service.ClearSkippedDeletes("folder1");

        var stats = _service.GetStats("folder1");
        Assert.Empty(stats.RecentSkippedDeletes);
    }

    [Fact]
    public void ClearSkippedDeletes_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ClearSkippedDeletes(null!));
    }

    #endregion
}

public class IgnoreDeletesConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new IgnoreDeletesConfiguration();

        Assert.False(config.DefaultIgnoreDeletes);
        Assert.True(config.LogSkippedDeletes);
        Assert.Equal(1000, config.MaxSkippedDeletesTracked);
        Assert.Empty(config.FolderSettings);
        Assert.Empty(config.DeviceSettings);
        Assert.Empty(config.IgnoreDeletePatterns);
    }
}
