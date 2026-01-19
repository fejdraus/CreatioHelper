using System.Runtime.InteropServices;
using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.FileSystem;

public class CopyRangeMethodSelectorTests
{
    private readonly Mock<ILogger<CopyRangeMethodSelector>> _loggerMock;
    private readonly CopyRangeMethodSelector _selector;

    public CopyRangeMethodSelectorTests()
    {
        _loggerMock = new Mock<ILogger<CopyRangeMethodSelector>>();
        _selector = new CopyRangeMethodSelector(_loggerMock.Object);
    }

    #region GetBestMethod Tests

    [Fact]
    public void GetBestMethod_ReturnsValidMethod()
    {
        var method = _selector.GetBestMethod();

        Assert.True(Enum.IsDefined(typeof(CopyRangeMethod), method));
    }

    [Fact]
    public void GetBestMethod_OnWindows_ReturnsWindowsCopy()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Skip on non-Windows
        }

        var method = _selector.GetBestMethod();

        Assert.Equal(CopyRangeMethod.WindowsCopy, method);
    }

    [Fact]
    public void GetBestMethod_OnLinux_ReturnsCopyFileRange()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return; // Skip on non-Linux
        }

        var method = _selector.GetBestMethod();

        Assert.Equal(CopyRangeMethod.CopyFileRange, method);
    }

    #endregion

    #region GetBestMethodForFilesystem Tests

    [Theory]
    [InlineData("btrfs", CopyRangeMethod.Reflink)]
    [InlineData("xfs", CopyRangeMethod.Reflink)]
    [InlineData("ocfs2", CopyRangeMethod.Reflink)]
    [InlineData("apfs", CopyRangeMethod.Reflink)]
    [InlineData("refs", CopyRangeMethod.Reflink)]
    public void GetBestMethodForFilesystem_CoWFilesystems_ReturnsReflink(string fsType, CopyRangeMethod expected)
    {
        var method = _selector.GetBestMethodForFilesystem(fsType);

        Assert.Equal(expected, method);
    }

    [Theory]
    [InlineData("nfs", CopyRangeMethod.Standard)]
    [InlineData("cifs", CopyRangeMethod.Standard)]
    [InlineData("smb", CopyRangeMethod.Standard)]
    [InlineData("sshfs", CopyRangeMethod.Standard)]
    public void GetBestMethodForFilesystem_NetworkFilesystems_ReturnsStandard(string fsType, CopyRangeMethod expected)
    {
        var method = _selector.GetBestMethodForFilesystem(fsType);

        Assert.Equal(expected, method);
    }

    [Fact]
    public void GetBestMethodForFilesystem_NullOrEmpty_ReturnsBestMethod()
    {
        var methodNull = _selector.GetBestMethodForFilesystem(null!);
        var methodEmpty = _selector.GetBestMethodForFilesystem("");

        Assert.Equal(_selector.GetBestMethod(), methodNull);
        Assert.Equal(_selector.GetBestMethod(), methodEmpty);
    }

    #endregion

    #region IsMethodSupported Tests

    [Fact]
    public void IsMethodSupported_Standard_AlwaysTrue()
    {
        Assert.True(_selector.IsMethodSupported(CopyRangeMethod.Standard));
    }

    [Fact]
    public void IsMethodSupported_Auto_AlwaysTrue()
    {
        Assert.True(_selector.IsMethodSupported(CopyRangeMethod.Auto));
    }

    [Fact]
    public void IsMethodSupported_PlatformSpecific_DependsOnPlatform()
    {
        var windowsCopySupported = _selector.IsMethodSupported(CopyRangeMethod.WindowsCopy);
        var copyFileRangeSupported = _selector.IsMethodSupported(CopyRangeMethod.CopyFileRange);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.True(windowsCopySupported);
            Assert.False(copyFileRangeSupported);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.False(windowsCopySupported);
            Assert.True(copyFileRangeSupported);
        }
    }

    #endregion

    #region GetMethodInfo Tests

    [Theory]
    [InlineData(CopyRangeMethod.Standard)]
    [InlineData(CopyRangeMethod.Auto)]
    [InlineData(CopyRangeMethod.Reflink)]
    [InlineData(CopyRangeMethod.CopyFileRange)]
    [InlineData(CopyRangeMethod.WindowsCopy)]
    public void GetMethodInfo_ReturnsValidInfo(CopyRangeMethod method)
    {
        var info = _selector.GetMethodInfo(method);

        Assert.NotNull(info);
        Assert.Equal(method, info.Method);
        Assert.NotEmpty(info.Name);
        Assert.NotEmpty(info.Description);
    }

    [Fact]
    public void GetMethodInfo_Reflink_HasCoWFlag()
    {
        var info = _selector.GetMethodInfo(CopyRangeMethod.Reflink);

        Assert.True(info.IsCoW);
        Assert.True(info.RequiresFilesystemSupport);
        Assert.Contains("btrfs", info.SupportedFilesystems);
    }

    [Fact]
    public void GetMethodInfo_Standard_NoSpecialRequirements()
    {
        var info = _selector.GetMethodInfo(CopyRangeMethod.Standard);

        Assert.False(info.RequiresKernelSupport);
        Assert.False(info.RequiresFilesystemSupport);
        Assert.False(info.IsCoW);
    }

    #endregion

    #region GetSupportedMethods Tests

    [Fact]
    public void GetSupportedMethods_AlwaysIncludesStandard()
    {
        var methods = _selector.GetSupportedMethods();

        Assert.Contains(CopyRangeMethod.Standard, methods);
    }

    [Fact]
    public void GetSupportedMethods_ReturnsNonEmptyList()
    {
        var methods = _selector.GetSupportedMethods();

        Assert.NotEmpty(methods);
    }

    [Fact]
    public void GetSupportedMethods_OnWindows_IncludesWindowsCopy()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var methods = _selector.GetSupportedMethods();

        Assert.Contains(CopyRangeMethod.WindowsCopy, methods);
    }

    [Fact]
    public void GetSupportedMethods_OnLinux_IncludesLinuxMethods()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var methods = _selector.GetSupportedMethods();

        Assert.Contains(CopyRangeMethod.CopyFileRange, methods);
        Assert.Contains(CopyRangeMethod.SendFile, methods);
    }

    #endregion

    #region TestReflinkSupport Tests

    [Fact]
    public void TestReflinkSupport_NullPath_ReturnsFalse()
    {
        Assert.False(_selector.TestReflinkSupport(null!));
    }

    [Fact]
    public void TestReflinkSupport_EmptyPath_ReturnsFalse()
    {
        Assert.False(_selector.TestReflinkSupport(""));
    }

    [Fact]
    public void TestReflinkSupport_NonExistentPath_ReturnsFalse()
    {
        Assert.False(_selector.TestReflinkSupport("/nonexistent/path/for/testing"));
    }

    #endregion
}

public class CopyRangeConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new CopyRangeConfiguration();

        Assert.Equal(CopyRangeMethod.Auto, config.PreferredMethod);
        Assert.Equal(CopyRangeMethod.Standard, config.FallbackMethod);
        Assert.True(config.EnableReflink);
        Assert.Equal(64 * 1024, config.MinFileSizeForOptimizedCopy);
    }

    [Fact]
    public void FolderMethods_CanBeSet()
    {
        var config = new CopyRangeConfiguration();
        config.FolderMethods["folder1"] = CopyRangeMethod.Reflink;

        Assert.Equal(CopyRangeMethod.Reflink, config.FolderMethods["folder1"]);
    }
}

public class CopyMethodInfoTests
{
    [Fact]
    public void Record_PropertiesWork()
    {
        var info = new CopyMethodInfo
        {
            Method = CopyRangeMethod.Reflink,
            Name = "Reflink",
            Description = "CoW reflink",
            IsSupported = true,
            IsCoW = true,
            PreservesHoles = true
        };

        Assert.Equal(CopyRangeMethod.Reflink, info.Method);
        Assert.Equal("Reflink", info.Name);
        Assert.True(info.IsCoW);
        Assert.True(info.PreservesHoles);
    }
}
