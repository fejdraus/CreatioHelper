using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.FileSystem;

public class IgnorePermissionsServiceTests
{
    private readonly Mock<ILogger<IgnorePermissionsService>> _loggerMock;
    private readonly IgnorePermissionsConfiguration _config;
    private readonly IgnorePermissionsService _service;

    public IgnorePermissionsServiceTests()
    {
        _loggerMock = new Mock<ILogger<IgnorePermissionsService>>();
        _config = new IgnorePermissionsConfiguration();
        _service = new IgnorePermissionsService(_loggerMock.Object, _config);
    }

    #region ShouldIgnorePermissions Tests

    [Fact]
    public void ShouldIgnorePermissions_Default_ReturnsFalse()
    {
        Assert.False(_service.ShouldIgnorePermissions("folder1"));
    }

    [Fact]
    public void ShouldIgnorePermissions_AfterSet_ReturnsTrue()
    {
        _service.SetIgnorePermissions("folder1", true);

        Assert.True(_service.ShouldIgnorePermissions("folder1"));
    }

    [Fact]
    public void ShouldIgnorePermissions_ConfigDefault_UsesDefault()
    {
        var config = new IgnorePermissionsConfiguration { DefaultIgnorePermissions = true };
        var service = new IgnorePermissionsService(_loggerMock.Object, config);

        Assert.True(service.ShouldIgnorePermissions("folder1"));
    }

    [Fact]
    public void ShouldIgnorePermissions_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldIgnorePermissions(null!));
    }

    #endregion

    #region SetIgnorePermissions Tests

    [Fact]
    public void SetIgnorePermissions_True_SetsCorrectly()
    {
        _service.SetIgnorePermissions("folder1", true);

        Assert.True(_service.ShouldIgnorePermissions("folder1"));
    }

    [Fact]
    public void SetIgnorePermissions_False_SetsCorrectly()
    {
        _service.SetIgnorePermissions("folder1", true);
        _service.SetIgnorePermissions("folder1", false);

        Assert.False(_service.ShouldIgnorePermissions("folder1"));
    }

    [Fact]
    public void SetIgnorePermissions_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetIgnorePermissions(null!, true));
    }

    #endregion

    #region ArePermissionsEqual Tests

    [Fact]
    public void ArePermissionsEqual_SamePermissions_ReturnsTrue()
    {
        var perm1 = FilePermissions.FromMode(0x1A4); // 0644
        var perm2 = FilePermissions.FromMode(0x1A4);

        Assert.True(_service.ArePermissionsEqual("folder1", perm1, perm2));
    }

    [Fact]
    public void ArePermissionsEqual_DifferentPermissions_ReturnsFalse()
    {
        var perm1 = FilePermissions.FromMode(0x1A4); // 0644
        var perm2 = FilePermissions.FromMode(0x1ED); // 0755

        Assert.False(_service.ArePermissionsEqual("folder1", perm1, perm2));
    }

    [Fact]
    public void ArePermissionsEqual_IgnoringPermissions_ReturnsTrue()
    {
        _service.SetIgnorePermissions("folder1", true);

        var perm1 = FilePermissions.FromMode(0x1A4); // 0644
        var perm2 = FilePermissions.FromMode(0x180); // 0600

        Assert.True(_service.ArePermissionsEqual("folder1", perm1, perm2));
    }

    [Fact]
    public void ArePermissionsEqual_IgnoringWithPreserveExecute_ComparesExecuteBit()
    {
        var config = new IgnorePermissionsConfiguration
        {
            DefaultIgnorePermissions = true,
            PreserveExecuteBit = true
        };
        var service = new IgnorePermissionsService(_loggerMock.Object, config);

        var nonExec = FilePermissions.FromMode(0x1A4); // 0644
        var exec = FilePermissions.FromMode(0x1ED); // 0755

        Assert.False(service.ArePermissionsEqual("folder1", nonExec, exec));
    }

    [Fact]
    public void ArePermissionsEqual_IgnoringWithoutPreserveExecute_IgnoresExecuteBit()
    {
        var config = new IgnorePermissionsConfiguration
        {
            DefaultIgnorePermissions = true,
            PreserveExecuteBit = false
        };
        var service = new IgnorePermissionsService(_loggerMock.Object, config);

        var nonExec = FilePermissions.FromMode(0x1A4); // 0644
        var exec = FilePermissions.FromMode(0x1ED); // 0755

        Assert.True(service.ArePermissionsEqual("folder1", nonExec, exec));
    }

    #endregion

    #region GetEffectivePermissions Tests

    [Fact]
    public void GetEffectivePermissions_NotIgnoring_ReturnsRequested()
    {
        var requested = FilePermissions.FromMode(0x1FF); // 0777

        var result = _service.GetEffectivePermissions("folder1", requested);

        Assert.Equal(requested, result);
    }

    [Fact]
    public void GetEffectivePermissions_IgnoringWithExisting_ReturnsExisting()
    {
        _service.SetIgnorePermissions("folder1", true);
        // Request non-executable so execute bit doesn't change existing
        var requested = FilePermissions.FromMode(0x1A4); // 0644 (non-executable)
        var existing = FilePermissions.FromMode(0x1A4); // 0644

        var result = _service.GetEffectivePermissions("folder1", requested, existing);

        Assert.Equal(existing, result);
    }

    [Fact]
    public void GetEffectivePermissions_IgnoringWithoutExisting_ReturnsDefault()
    {
        _service.SetIgnorePermissions("folder1", true);
        // Use non-executable request so execute bit preservation doesn't affect result
        var requested = FilePermissions.FromMode(0x1A4); // 0644 (non-executable)

        var result = _service.GetEffectivePermissions("folder1", requested);

        Assert.Equal(_config.DefaultFileMode & 0x1FF, result.Mode);
    }

    [Fact]
    public void GetEffectivePermissions_PreservesExecuteBit()
    {
        _service.SetIgnorePermissions("folder1", true);
        var requested = FilePermissions.FromMode(0x1ED); // 0755 (executable)

        var result = _service.GetEffectivePermissions("folder1", requested);

        Assert.True(result.IsExecutable);
    }

    [Fact]
    public void GetEffectivePermissions_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetEffectivePermissions(null!, FilePermissions.DefaultFile));
    }

    #endregion

    #region GetDefaultPermissions Tests

    [Fact]
    public void GetDefaultFilePermissions_ReturnsConfigured()
    {
        var result = _service.GetDefaultFilePermissions("folder1");

        Assert.Equal(_config.DefaultFileMode, result.Mode);
    }

    [Fact]
    public void GetDefaultDirectoryPermissions_ReturnsConfigured()
    {
        var result = _service.GetDefaultDirectoryPermissions("folder1");

        Assert.Equal(_config.DefaultDirectoryMode, result.Mode);
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_NewFolder_ReturnsEmptyStats()
    {
        var stats = _service.GetStats("folder1");

        Assert.Equal("folder1", stats.FolderId);
        Assert.Equal(0, stats.FilesWithPermissionsApplied);
        Assert.Equal(0, stats.FilesWithPermissionsIgnored);
    }

    [Fact]
    public void GetStats_AfterOperations_TracksCorrectly()
    {
        _service.GetEffectivePermissions("folder1", FilePermissions.DefaultFile);
        _service.GetEffectivePermissions("folder1", FilePermissions.DefaultFile);

        var stats = _service.GetStats("folder1");

        Assert.Equal(2, stats.FilesWithPermissionsApplied);
    }

    [Fact]
    public void GetStats_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStats(null!));
    }

    #endregion
}

public class FilePermissionsTests
{
    [Fact]
    public void DefaultFile_Is0644()
    {
        var perm = FilePermissions.DefaultFile;

        Assert.True(perm.OwnerRead);
        Assert.True(perm.OwnerWrite);
        Assert.False(perm.OwnerExecute);
        Assert.True(perm.GroupRead);
        Assert.False(perm.GroupWrite);
        Assert.False(perm.GroupExecute);
        Assert.True(perm.OtherRead);
        Assert.False(perm.OtherWrite);
        Assert.False(perm.OtherExecute);
    }

    [Fact]
    public void DefaultDirectory_Is0755()
    {
        var perm = FilePermissions.DefaultDirectory;

        Assert.True(perm.OwnerRead);
        Assert.True(perm.OwnerWrite);
        Assert.True(perm.OwnerExecute);
        Assert.True(perm.GroupRead);
        Assert.False(perm.GroupWrite);
        Assert.True(perm.GroupExecute);
        Assert.True(perm.OtherRead);
        Assert.False(perm.OtherWrite);
        Assert.True(perm.OtherExecute);
    }

    [Fact]
    public void FromOctal_ParsesCorrectly()
    {
        var perm = FilePermissions.FromOctal("0755");

        Assert.Equal(0x1ED, perm.Mode);
        Assert.True(perm.IsExecutable);
    }

    [Fact]
    public void FromMode_MasksTo9Bits()
    {
        var perm = FilePermissions.FromMode(0xFFFF);

        Assert.Equal(0x1FF, perm.Mode); // Only lower 9 bits
    }

    [Fact]
    public void ToString_ReturnsOctal()
    {
        var perm = FilePermissions.FromMode(0x1A4);

        Assert.Equal("0644", perm.ToString());
    }

    [Fact]
    public void Equality_SameMode_AreEqual()
    {
        var perm1 = FilePermissions.FromMode(0x1A4);
        var perm2 = FilePermissions.FromMode(0x1A4);

        Assert.Equal(perm1, perm2);
        Assert.True(perm1 == perm2);
    }

    [Fact]
    public void Equality_DifferentMode_NotEqual()
    {
        var perm1 = FilePermissions.FromMode(0x1A4);
        var perm2 = FilePermissions.FromMode(0x1ED);

        Assert.NotEqual(perm1, perm2);
        Assert.True(perm1 != perm2);
    }
}

public class IgnorePermissionsConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new IgnorePermissionsConfiguration();

        Assert.False(config.DefaultIgnorePermissions);
        Assert.Equal(0x1A4, config.DefaultFileMode); // 0644
        Assert.Equal(0x1ED, config.DefaultDirectoryMode); // 0755
        Assert.True(config.PreserveExecuteBit);
    }

    [Fact]
    public void GetEffectiveSetting_NoOverride_ReturnsDefault()
    {
        var config = new IgnorePermissionsConfiguration { DefaultIgnorePermissions = true };

        Assert.True(config.GetEffectiveSetting("folder1"));
    }

    [Fact]
    public void GetEffectiveSetting_WithOverride_ReturnsOverride()
    {
        var config = new IgnorePermissionsConfiguration { DefaultIgnorePermissions = false };
        config.FolderSettings["folder1"] = true;

        Assert.True(config.GetEffectiveSetting("folder1"));
        Assert.False(config.GetEffectiveSetting("folder2"));
    }
}
