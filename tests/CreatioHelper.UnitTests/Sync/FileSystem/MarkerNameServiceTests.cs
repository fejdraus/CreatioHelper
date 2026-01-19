using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.FileSystem;

public class MarkerNameServiceTests
{
    private readonly Mock<ILogger<MarkerNameService>> _loggerMock;
    private readonly MarkerNameConfiguration _config;
    private readonly MarkerNameService _service;

    public MarkerNameServiceTests()
    {
        _loggerMock = new Mock<ILogger<MarkerNameService>>();
        _config = new MarkerNameConfiguration();
        _service = new MarkerNameService(_loggerMock.Object, _config);
    }

    #region GetMarkerName Tests

    [Fact]
    public void GetMarkerName_Default_ReturnsStfolder()
    {
        var markerName = _service.GetMarkerName("folder1");

        Assert.Equal(".stfolder", markerName);
    }

    [Fact]
    public void GetMarkerName_Custom_ReturnsCustom()
    {
        _service.SetMarkerName("folder1", ".custom-marker");

        var markerName = _service.GetMarkerName("folder1");

        Assert.Equal(".custom-marker", markerName);
    }

    [Fact]
    public void GetMarkerName_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetMarkerName(null!));
    }

    #endregion

    #region SetMarkerName Tests

    [Fact]
    public void SetMarkerName_ValidName_Sets()
    {
        _service.SetMarkerName("folder1", ".my-marker");

        Assert.Equal(".my-marker", _service.GetMarkerName("folder1"));
    }

    [Fact]
    public void SetMarkerName_UpdatesConfig()
    {
        _service.SetMarkerName("folder1", ".my-marker");

        Assert.Equal(".my-marker", _config.FolderMarkerNames["folder1"]);
    }

    [Fact]
    public void SetMarkerName_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetMarkerName(null!, ".marker"));
    }

    [Fact]
    public void SetMarkerName_NullMarkerName_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetMarkerName("folder1", null!));
    }

    [Fact]
    public void SetMarkerName_InvalidName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.SetMarkerName("folder1", ""));
    }

    [Fact]
    public void SetMarkerName_WithPathTraversal_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.SetMarkerName("folder1", "..\\marker"));
    }

    #endregion

    #region ResetMarkerName Tests

    [Fact]
    public void ResetMarkerName_Custom_ResetsToDefault()
    {
        _service.SetMarkerName("folder1", ".custom");

        _service.ResetMarkerName("folder1");

        Assert.Equal(".stfolder", _service.GetMarkerName("folder1"));
    }

    [Fact]
    public void ResetMarkerName_RemovesFromConfig()
    {
        _service.SetMarkerName("folder1", ".custom");

        _service.ResetMarkerName("folder1");

        Assert.False(_config.FolderMarkerNames.ContainsKey("folder1"));
    }

    [Fact]
    public void ResetMarkerName_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ResetMarkerName(null!));
    }

    #endregion

    #region IsMarkerFile Tests

    [Fact]
    public void IsMarkerFile_MatchesDefault_ReturnsTrue()
    {
        Assert.True(_service.IsMarkerFile("folder1", ".stfolder"));
        Assert.True(_service.IsMarkerFile("folder1", "/path/to/.stfolder"));
    }

    [Fact]
    public void IsMarkerFile_MatchesCustom_ReturnsTrue()
    {
        _service.SetMarkerName("folder1", ".custom");

        Assert.True(_service.IsMarkerFile("folder1", ".custom"));
        Assert.True(_service.IsMarkerFile("folder1", "/path/to/.custom"));
    }

    [Fact]
    public void IsMarkerFile_DoesNotMatch_ReturnsFalse()
    {
        Assert.False(_service.IsMarkerFile("folder1", "other-file"));
        Assert.False(_service.IsMarkerFile("folder1", ".stversion"));
    }

    [Fact]
    public void IsMarkerFile_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(_service.IsMarkerFile("folder1", ".STFOLDER"));
        Assert.True(_service.IsMarkerFile("folder1", ".StFolder"));
    }

    [Fact]
    public void IsMarkerFile_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsMarkerFile(null!, ".stfolder"));
    }

    [Fact]
    public void IsMarkerFile_NullPath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsMarkerFile("folder1", null!));
    }

    #endregion

    #region GetMarkerPath Tests

    [Fact]
    public void GetMarkerPath_CombinesFolderAndMarker()
    {
        var path = _service.GetMarkerPath("folder1", "/data/sync");

        Assert.Equal(Path.Combine("/data/sync", ".stfolder"), path);
    }

    [Fact]
    public void GetMarkerPath_UsesCustomMarker()
    {
        _service.SetMarkerName("folder1", ".custom");

        var path = _service.GetMarkerPath("folder1", "/data/sync");

        Assert.Equal(Path.Combine("/data/sync", ".custom"), path);
    }

    [Fact]
    public void GetMarkerPath_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetMarkerPath(null!, "/data"));
    }

    [Fact]
    public void GetMarkerPath_NullFolderPath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetMarkerPath("folder1", null!));
    }

    #endregion

    #region IsValidMarkerName Tests

    [Fact]
    public void IsValidMarkerName_ValidNames_ReturnsTrue()
    {
        Assert.True(_service.IsValidMarkerName(".stfolder"));
        Assert.True(_service.IsValidMarkerName(".marker"));
        Assert.True(_service.IsValidMarkerName("marker"));
        Assert.True(_service.IsValidMarkerName("my-marker"));
        Assert.True(_service.IsValidMarkerName("marker_123"));
    }

    [Fact]
    public void IsValidMarkerName_Empty_ReturnsFalse()
    {
        Assert.False(_service.IsValidMarkerName(""));
        Assert.False(_service.IsValidMarkerName(" "));
    }

    [Fact]
    public void IsValidMarkerName_PathTraversal_ReturnsFalse()
    {
        Assert.False(_service.IsValidMarkerName(".."));
        Assert.False(_service.IsValidMarkerName("../marker"));
        Assert.False(_service.IsValidMarkerName("marker/.."));
    }

    [Fact]
    public void IsValidMarkerName_WithSlashes_ReturnsFalse()
    {
        Assert.False(_service.IsValidMarkerName("path/marker"));
        Assert.False(_service.IsValidMarkerName("path\\marker"));
    }

    [Fact]
    public void IsValidMarkerName_ReservedNames_ReturnsFalse()
    {
        Assert.False(_service.IsValidMarkerName("CON"));
        Assert.False(_service.IsValidMarkerName("PRN"));
        Assert.False(_service.IsValidMarkerName("NUL"));
        Assert.False(_service.IsValidMarkerName("COM1"));
        Assert.False(_service.IsValidMarkerName("LPT1"));
    }

    [Fact]
    public void IsValidMarkerName_TooLong_ReturnsFalse()
    {
        var longName = new string('a', 300);

        Assert.False(_service.IsValidMarkerName(longName));
    }

    #endregion

    #region GetAllMarkerNames Tests

    [Fact]
    public void GetAllMarkerNames_Initial_ReturnsEmpty()
    {
        var names = _service.GetAllMarkerNames();

        Assert.Empty(names);
    }

    [Fact]
    public void GetAllMarkerNames_WithCustomNames_ReturnsAll()
    {
        _service.SetMarkerName("folder1", ".marker1");
        _service.SetMarkerName("folder2", ".marker2");

        var names = _service.GetAllMarkerNames();

        Assert.Equal(2, names.Count);
        Assert.Equal(".marker1", names["folder1"]);
        Assert.Equal(".marker2", names["folder2"]);
    }

    #endregion

    #region EnsureMarkerExists and MarkerExists Tests

    [Fact]
    public void EnsureMarkerExists_CreatesMarkerDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = _service.EnsureMarkerExists("folder1", tempDir);

            Assert.True(result);
            Assert.True(Directory.Exists(Path.Combine(tempDir, ".stfolder")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MarkerExists_NoMarker_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            Assert.False(_service.MarkerExists("folder1", tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MarkerExists_WithMarker_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            _service.EnsureMarkerExists("folder1", tempDir);

            Assert.True(_service.MarkerExists("folder1", tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnsureMarkerExists_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.EnsureMarkerExists(null!, "/path"));
    }

    [Fact]
    public void EnsureMarkerExists_NullFolderPath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.EnsureMarkerExists("folder1", null!));
    }

    [Fact]
    public void MarkerExists_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.MarkerExists(null!, "/path"));
    }

    [Fact]
    public void MarkerExists_NullFolderPath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.MarkerExists("folder1", null!));
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_NewFolder_ReturnsEmptyStats()
    {
        var stats = _service.GetStats("folder1");

        Assert.Equal("folder1", stats.FolderId);
        Assert.Equal(".stfolder", stats.MarkerName);
        Assert.False(stats.MarkerExists);
        Assert.Null(stats.LastChecked);
        Assert.Equal(0, stats.CheckCount);
        Assert.Equal(0, stats.CreateCount);
    }

    [Fact]
    public void GetStats_AfterMarkerCheck_UpdatesStats()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            _service.MarkerExists("folder1", tempDir);

            var stats = _service.GetStats("folder1");

            Assert.Equal(1, stats.CheckCount);
            Assert.NotNull(stats.LastChecked);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetStats_AfterMarkerCreate_UpdatesStats()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            _service.EnsureMarkerExists("folder1", tempDir);

            var stats = _service.GetStats("folder1");

            Assert.Equal(1, stats.CreateCount);
            Assert.NotNull(stats.MarkerCreatedAt);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetStats_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStats(null!));
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void InitialConfig_LoadsMarkerNames()
    {
        var config = new MarkerNameConfiguration();
        config.FolderMarkerNames["folder1"] = ".custom";
        var service = new MarkerNameService(_loggerMock.Object, config);

        Assert.Equal(".custom", service.GetMarkerName("folder1"));
    }

    [Fact]
    public void DefaultMarkerName_ChangesDefault()
    {
        var config = new MarkerNameConfiguration { DefaultMarkerName = ".syncthing" };
        var service = new MarkerNameService(_loggerMock.Object, config);

        Assert.Equal(".syncthing", service.GetMarkerName("folder1"));
    }

    #endregion
}

public class MarkerNameConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new MarkerNameConfiguration();

        Assert.Equal(".stfolder", config.DefaultMarkerName);
        Assert.Empty(config.FolderMarkerNames);
        Assert.Equal(255, config.MaxMarkerNameLength);
        Assert.True(config.PreferHiddenMarker);
        Assert.True(config.CreateAsDirectory);
    }
}
