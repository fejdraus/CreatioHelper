using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.FileSystem;

public class XAttrsOwnershipToggleTests
{
    private readonly Mock<ILogger<XAttrsOwnershipToggle>> _loggerMock;
    private readonly XAttrFilterConfiguration _config;
    private readonly XAttrsOwnershipToggle _toggle;

    public XAttrsOwnershipToggleTests()
    {
        _loggerMock = new Mock<ILogger<XAttrsOwnershipToggle>>();
        _config = new XAttrFilterConfiguration();
        _toggle = new XAttrsOwnershipToggle(_loggerMock.Object, _config);
    }

    private SyncFolder CreateFolder(
        bool syncXattrs = false,
        bool sendXattrs = false,
        bool syncOwnership = false,
        bool sendOwnership = false)
    {
        // Using reflection to set private properties for testing
        var folder = new SyncFolder("test", "Test Folder", "/test/path");

        var type = typeof(SyncFolder);
        type.GetProperty("SyncXattrs")?.SetValue(folder, syncXattrs);
        type.GetProperty("SendXattrs")?.SetValue(folder, sendXattrs);
        type.GetProperty("SyncOwnership")?.SetValue(folder, syncOwnership);
        type.GetProperty("SendOwnership")?.SetValue(folder, sendOwnership);

        return folder;
    }

    #region ShouldSyncXAttrs Tests

    [Fact]
    public void ShouldSyncXAttrs_Enabled_ReturnsTrue()
    {
        var folder = CreateFolder(syncXattrs: true);

        Assert.True(_toggle.ShouldSyncXAttrs(folder));
    }

    [Fact]
    public void ShouldSyncXAttrs_Disabled_ReturnsFalse()
    {
        var folder = CreateFolder(syncXattrs: false);

        Assert.False(_toggle.ShouldSyncXAttrs(folder));
    }

    [Fact]
    public void ShouldSyncXAttrs_NullFolder_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _toggle.ShouldSyncXAttrs(null!));
    }

    #endregion

    #region ShouldSendXAttrs Tests

    [Fact]
    public void ShouldSendXAttrs_Enabled_ReturnsTrue()
    {
        var folder = CreateFolder(sendXattrs: true);

        Assert.True(_toggle.ShouldSendXAttrs(folder));
    }

    [Fact]
    public void ShouldSendXAttrs_Disabled_ReturnsFalse()
    {
        var folder = CreateFolder(sendXattrs: false);

        Assert.False(_toggle.ShouldSendXAttrs(folder));
    }

    #endregion

    #region ShouldSyncOwnership Tests

    [Fact]
    public void ShouldSyncOwnership_Enabled_ReturnsTrue()
    {
        var folder = CreateFolder(syncOwnership: true);

        Assert.True(_toggle.ShouldSyncOwnership(folder));
    }

    [Fact]
    public void ShouldSyncOwnership_Disabled_ReturnsFalse()
    {
        var folder = CreateFolder(syncOwnership: false);

        Assert.False(_toggle.ShouldSyncOwnership(folder));
    }

    #endregion

    #region ShouldSendOwnership Tests

    [Fact]
    public void ShouldSendOwnership_Enabled_ReturnsTrue()
    {
        var folder = CreateFolder(sendOwnership: true);

        Assert.True(_toggle.ShouldSendOwnership(folder));
    }

    [Fact]
    public void ShouldSendOwnership_Disabled_ReturnsFalse()
    {
        var folder = CreateFolder(sendOwnership: false);

        Assert.False(_toggle.ShouldSendOwnership(folder));
    }

    #endregion

    #region FilterXAttrsAsync Tests

    [Fact]
    public async Task FilterXAttrsAsync_SyncDisabled_ReturnsEmpty()
    {
        var folder = CreateFolder(syncXattrs: false);
        var xattrs = new Dictionary<string, byte[]>
        {
            ["user.test"] = new byte[] { 1, 2, 3 }
        };

        var result = await _toggle.FilterXAttrsAsync(folder, xattrs, XAttrDirection.Receive);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterXAttrsAsync_SendDisabled_ReturnsEmpty()
    {
        var folder = CreateFolder(sendXattrs: false);
        var xattrs = new Dictionary<string, byte[]>
        {
            ["user.test"] = new byte[] { 1, 2, 3 }
        };

        var result = await _toggle.FilterXAttrsAsync(folder, xattrs, XAttrDirection.Send);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterXAttrsAsync_SyncEnabled_ReturnsXAttrs()
    {
        var folder = CreateFolder(syncXattrs: true);
        var xattrs = new Dictionary<string, byte[]>
        {
            ["user.test"] = new byte[] { 1, 2, 3 }
        };

        var result = await _toggle.FilterXAttrsAsync(folder, xattrs, XAttrDirection.Receive);

        Assert.Single(result);
        Assert.Contains("user.test", result.Keys);
    }

    [Fact]
    public async Task FilterXAttrsAsync_ExcludedPattern_Filtered()
    {
        var folder = CreateFolder(syncXattrs: true);
        var xattrs = new Dictionary<string, byte[]>
        {
            ["user.test"] = new byte[] { 1, 2, 3 },
            ["system.hidden"] = new byte[] { 4, 5, 6 },
            ["security.selinux"] = new byte[] { 7, 8, 9 }
        };

        var result = await _toggle.FilterXAttrsAsync(folder, xattrs, XAttrDirection.Receive);

        Assert.Single(result);
        Assert.Contains("user.test", result.Keys);
        Assert.DoesNotContain("system.hidden", result.Keys);
        Assert.DoesNotContain("security.selinux", result.Keys);
    }

    [Fact]
    public async Task FilterXAttrsAsync_NullInput_ReturnsEmpty()
    {
        var folder = CreateFolder(syncXattrs: true);

        var result = await _toggle.FilterXAttrsAsync(folder, null!, XAttrDirection.Receive);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterXAttrsAsync_EmptyInput_ReturnsEmpty()
    {
        var folder = CreateFolder(syncXattrs: true);

        var result = await _toggle.FilterXAttrsAsync(folder, new Dictionary<string, byte[]>(), XAttrDirection.Receive);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterXAttrsAsync_OversizedValue_Filtered()
    {
        var folder = CreateFolder(syncXattrs: true);
        var largeValue = new byte[_config.MaxXAttrValueSize + 1];
        var xattrs = new Dictionary<string, byte[]>
        {
            ["user.large"] = largeValue,
            ["user.small"] = new byte[] { 1, 2, 3 }
        };

        var result = await _toggle.FilterXAttrsAsync(folder, xattrs, XAttrDirection.Receive);

        Assert.Single(result);
        Assert.Contains("user.small", result.Keys);
        Assert.DoesNotContain("user.large", result.Keys);
    }

    #endregion

    #region FilterOwnership Tests

    [Fact]
    public void FilterOwnership_SyncDisabled_ReturnsNull()
    {
        var folder = CreateFolder(syncOwnership: false);
        var ownership = new OwnershipInfo(1000, 1000, "user", "group");

        var result = _toggle.FilterOwnership(folder, ownership, XAttrDirection.Receive);

        Assert.Null(result);
    }

    [Fact]
    public void FilterOwnership_SendDisabled_ReturnsNull()
    {
        var folder = CreateFolder(sendOwnership: false);
        var ownership = new OwnershipInfo(1000, 1000, "user", "group");

        var result = _toggle.FilterOwnership(folder, ownership, XAttrDirection.Send);

        Assert.Null(result);
    }

    [Fact]
    public void FilterOwnership_SyncEnabled_ReturnsOwnership()
    {
        var folder = CreateFolder(syncOwnership: true);
        var ownership = new OwnershipInfo(1000, 1000, "user", "group");

        var result = _toggle.FilterOwnership(folder, ownership, XAttrDirection.Receive);

        Assert.NotNull(result);
        Assert.Equal(1000, result.Uid);
        Assert.Equal(1000, result.Gid);
    }

    [Fact]
    public void FilterOwnership_NullOwnership_ReturnsNull()
    {
        var folder = CreateFolder(syncOwnership: true);

        var result = _toggle.FilterOwnership(folder, null, XAttrDirection.Receive);

        Assert.Null(result);
    }

    #endregion
}

public class CachedXAttrsOwnershipToggleTests
{
    private readonly Mock<IXAttrsOwnershipToggle> _innerMock;
    private readonly CachedXAttrsOwnershipToggle _cached;

    public CachedXAttrsOwnershipToggleTests()
    {
        _innerMock = new Mock<IXAttrsOwnershipToggle>();
        _cached = new CachedXAttrsOwnershipToggle(_innerMock.Object);
    }

    private SyncFolder CreateFolder(string id = "test")
    {
        return new SyncFolder(id, "Test Folder", "/test/path");
    }

    [Fact]
    public void ShouldSyncXAttrs_CachesResult()
    {
        var folder = CreateFolder();
        _innerMock.Setup(x => x.ShouldSyncXAttrs(folder)).Returns(true);

        var result1 = _cached.ShouldSyncXAttrs(folder);
        var result2 = _cached.ShouldSyncXAttrs(folder);

        Assert.True(result1);
        Assert.True(result2);
        _innerMock.Verify(x => x.ShouldSyncXAttrs(folder), Times.Once);
    }

    [Fact]
    public void InvalidateCache_ClearsCache()
    {
        var folder = CreateFolder();
        _innerMock.Setup(x => x.ShouldSyncXAttrs(folder)).Returns(true);

        _cached.ShouldSyncXAttrs(folder);
        _cached.InvalidateCache("test");
        _cached.ShouldSyncXAttrs(folder);

        _innerMock.Verify(x => x.ShouldSyncXAttrs(folder), Times.Exactly(2));
    }

    [Fact]
    public void ClearCache_ClearsAllCaches()
    {
        var folder1 = CreateFolder("folder1");
        var folder2 = CreateFolder("folder2");
        _innerMock.Setup(x => x.ShouldSyncXAttrs(It.IsAny<SyncFolder>())).Returns(true);

        _cached.ShouldSyncXAttrs(folder1);
        _cached.ShouldSyncXAttrs(folder2);
        _cached.ClearCache();
        _cached.ShouldSyncXAttrs(folder1);
        _cached.ShouldSyncXAttrs(folder2);

        _innerMock.Verify(x => x.ShouldSyncXAttrs(folder1), Times.Exactly(2));
        _innerMock.Verify(x => x.ShouldSyncXAttrs(folder2), Times.Exactly(2));
    }
}

public class XAttrFilterConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new XAttrFilterConfiguration();

        Assert.Empty(config.IncludePatterns);
        Assert.NotEmpty(config.ExcludePatterns);
        Assert.Contains("system.*", config.ExcludePatterns);
        Assert.Contains("security.selinux", config.ExcludePatterns);
        Assert.Equal(64 * 1024, config.MaxXAttrValueSize);
        Assert.Equal(1024 * 1024, config.MaxTotalXAttrSize);
    }
}

public class OwnershipInfoTests
{
    [Fact]
    public void Record_Properties_Work()
    {
        var ownership = new OwnershipInfo(1000, 1001, "testuser", "testgroup");

        Assert.Equal(1000, ownership.Uid);
        Assert.Equal(1001, ownership.Gid);
        Assert.Equal("testuser", ownership.User);
        Assert.Equal("testgroup", ownership.Group);
    }

    [Fact]
    public void Record_NullValues_Allowed()
    {
        var ownership = new OwnershipInfo(null, null, null, null);

        Assert.Null(ownership.Uid);
        Assert.Null(ownership.Gid);
        Assert.Null(ownership.User);
        Assert.Null(ownership.Group);
    }
}
