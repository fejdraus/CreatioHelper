using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Xunit;

namespace CreatioHelper.UnitTests.Sync.Transfer;

public class DiskSpaceGuardTests
{
    private static string TempPath => Path.GetTempPath();

    [Fact]
    public void NoThresholdConfigured_Allows()
    {
        var check = DiskSpaceGuard.Check(TempPath, null, 1024);
        Assert.True(check.Allowed);

        check = DiskSpaceGuard.Check(TempPath, "  ", 1024);
        Assert.True(check.Allowed);
    }

    [Fact]
    public void ZeroThreshold_Allows()
    {
        var check = DiskSpaceGuard.Check(TempPath, "0%", long.MaxValue / 2);
        Assert.True(check.Allowed);
    }

    [Fact]
    public void ImpossibleThreshold_Blocks()
    {
        var check = DiskSpaceGuard.Check(TempPath, "99.9%", 0);
        Assert.False(check.Allowed);
        Assert.True(check.RequiredFreeBytes > 0);
        Assert.Contains("below the configured minimum", check.Reason);
    }

    [Fact]
    public void IncomingFileLargerThanDisk_Blocks()
    {
        var check = DiskSpaceGuard.Check(TempPath, "1MB", long.MaxValue / 4);
        Assert.False(check.Allowed);
    }

    [Fact]
    public void SmallThresholdAndSmallFile_Allows()
    {
        var check = DiskSpaceGuard.Check(TempPath, "1kB", 1024);
        Assert.True(check.Allowed);
    }

    [Fact]
    public void UnreadablePath_AllowsRatherThanBlockingSync()
    {
        var check = DiskSpaceGuard.Check("Z:\\definitely\\not\\mounted", "10%", 1024);
        Assert.True(check.Allowed);
    }

    [Theory]
    [InlineData("1%")]
    [InlineData("100MB")]
    [InlineData("2GB")]
    [InlineData("1TB")]
    [InlineData("500kB")]
    public void SupportedUnits_AreParsedAndProduceAThreshold(string minDiskFree)
    {
        var check = DiskSpaceGuard.Check(TempPath, minDiskFree, 0);
        Assert.True(check.RequiredFreeBytes > 0, $"единица {minDiskFree} не распознана");
    }
}
