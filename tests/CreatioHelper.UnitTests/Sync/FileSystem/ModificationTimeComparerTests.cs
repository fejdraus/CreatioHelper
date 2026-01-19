using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Xunit;

namespace CreatioHelper.Tests.Sync.FileSystem;

public class ModificationTimeComparerTests
{
    private readonly ModificationTimeComparer _comparer = new();

    #region AreTimesEqual Tests

    [Fact]
    public void AreTimesEqual_ExactMatch_ReturnsTrue()
    {
        var time = DateTime.UtcNow;
        Assert.True(_comparer.AreTimesEqual(time, time, 0));
    }

    [Fact]
    public void AreTimesEqual_ZeroWindow_RequiresExactMatch()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddMilliseconds(1);
        Assert.False(_comparer.AreTimesEqual(time1, time2, 0));
    }

    [Fact]
    public void AreTimesEqual_WithinWindow_ReturnsTrue()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(1);
        Assert.True(_comparer.AreTimesEqual(time1, time2, 2));
    }

    [Fact]
    public void AreTimesEqual_ExactlyAtWindow_ReturnsTrue()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(2);
        Assert.True(_comparer.AreTimesEqual(time1, time2, 2));
    }

    [Fact]
    public void AreTimesEqual_OutsideWindow_ReturnsFalse()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(3);
        Assert.False(_comparer.AreTimesEqual(time1, time2, 2));
    }

    [Fact]
    public void AreTimesEqual_NegativeWindow_TreatedAsZero()
    {
        var time = DateTime.UtcNow;
        Assert.True(_comparer.AreTimesEqual(time, time, -1));
    }

    #endregion

    #region IsNewer Tests

    [Fact]
    public void IsNewer_ClearlyNewer_ReturnsTrue()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(-10);
        Assert.True(_comparer.IsNewer(time1, time2, 2));
    }

    [Fact]
    public void IsNewer_WithinWindow_ReturnsFalse()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(-1);
        Assert.False(_comparer.IsNewer(time1, time2, 2));
    }

    [Fact]
    public void IsNewer_ExactlyAtWindow_ReturnsFalse()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(-2);
        Assert.False(_comparer.IsNewer(time1, time2, 2));
    }

    [Fact]
    public void IsNewer_ZeroWindow_Exact()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddMilliseconds(-1);
        Assert.True(_comparer.IsNewer(time1, time2, 0));
    }

    #endregion

    #region IsOlder Tests

    [Fact]
    public void IsOlder_ClearlyOlder_ReturnsTrue()
    {
        var time1 = DateTime.UtcNow.AddSeconds(-10);
        var time2 = DateTime.UtcNow;
        Assert.True(_comparer.IsOlder(time1, time2, 2));
    }

    [Fact]
    public void IsOlder_WithinWindow_ReturnsFalse()
    {
        var time1 = DateTime.UtcNow.AddSeconds(-1);
        var time2 = DateTime.UtcNow;
        Assert.False(_comparer.IsOlder(time1, time2, 2));
    }

    #endregion

    #region GetRecommendedWindowForFilesystem Tests

    [Theory]
    [InlineData("fat", 2)]
    [InlineData("fat32", 2)]
    [InlineData("FAT32", 2)]
    [InlineData("vfat", 2)]
    [InlineData("exfat", 1)]
    [InlineData("ntfs", 0)]
    [InlineData("NTFS", 0)]
    [InlineData("ext4", 0)]
    [InlineData("apfs", 0)]
    [InlineData("hfs+", 1)]
    [InlineData("nfs", 1)]
    [InlineData("smb", 2)]
    [InlineData("cifs", 2)]
    [InlineData("sshfs", 1)]
    [InlineData("basic", 0)]
    [InlineData("unknown", 0)]
    [InlineData("", 0)]
    public void GetRecommendedWindowForFilesystem_ReturnsExpectedValue(string fsType, int expected)
    {
        Assert.Equal(expected, _comparer.GetRecommendedWindowForFilesystem(fsType));
    }

    [Fact]
    public void GetRecommendedWindowForFilesystem_NullInput_ReturnsZero()
    {
        Assert.Equal(0, _comparer.GetRecommendedWindowForFilesystem(null!));
    }

    #endregion

    #region Extension Methods Tests

    [Fact]
    public void Compare_ReturnsEqual_WhenTimesWithinWindow()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(1);
        Assert.Equal(ModificationTimeComparison.Equal, _comparer.Compare(time1, time2, 2));
    }

    [Fact]
    public void Compare_ReturnsNewer_WhenTime1IsNewer()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(-5);
        Assert.Equal(ModificationTimeComparison.Newer, _comparer.Compare(time1, time2, 2));
    }

    [Fact]
    public void Compare_ReturnsOlder_WhenTime1IsOlder()
    {
        var time1 = DateTime.UtcNow.AddSeconds(-5);
        var time2 = DateTime.UtcNow;
        Assert.Equal(ModificationTimeComparison.Older, _comparer.Compare(time1, time2, 2));
    }

    [Fact]
    public void NeedsUpdate_LocalOlder_ReturnsTrue()
    {
        var localTime = DateTime.UtcNow.AddSeconds(-10);
        var remoteTime = DateTime.UtcNow;
        Assert.True(_comparer.NeedsUpdate(localTime, remoteTime, 2));
    }

    [Fact]
    public void NeedsUpdate_LocalNewer_ReturnsFalse()
    {
        var localTime = DateTime.UtcNow;
        var remoteTime = DateTime.UtcNow.AddSeconds(-10);
        Assert.False(_comparer.NeedsUpdate(localTime, remoteTime, 2));
    }

    [Fact]
    public void NeedsUpdate_WithinWindow_ReturnsFalse()
    {
        var localTime = DateTime.UtcNow.AddSeconds(-1);
        var remoteTime = DateTime.UtcNow;
        Assert.False(_comparer.NeedsUpdate(localTime, remoteTime, 2));
    }

    #endregion

    #region Real-World Scenarios

    [Fact]
    public void FatFilesystem_TwoSecondResolution_HandledCorrectly()
    {
        // FAT filesystems have 2-second resolution
        var window = _comparer.GetRecommendedWindowForFilesystem("fat32");

        var time1 = new DateTime(2024, 1, 1, 12, 0, 0);
        var time2 = new DateTime(2024, 1, 1, 12, 0, 2);

        // These times should be considered equal on FAT
        Assert.True(_comparer.AreTimesEqual(time1, time2, window));
    }

    [Fact]
    public void NetworkFilesystem_ClockSkew_HandledCorrectly()
    {
        // NFS can have clock skew issues
        var window = _comparer.GetRecommendedWindowForFilesystem("nfs");

        var time1 = DateTime.UtcNow;
        var time2 = time1.AddMilliseconds(500); // Half second difference

        Assert.True(_comparer.AreTimesEqual(time1, time2, window));
    }

    #endregion
}
