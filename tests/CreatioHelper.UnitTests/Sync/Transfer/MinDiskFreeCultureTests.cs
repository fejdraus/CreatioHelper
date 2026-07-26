using System.Globalization;
using CreatioHelper.Application.DTOs;
using CreatioHelper.Domain.Entities;
using Xunit;

namespace CreatioHelper.UnitTests.Sync.Transfer;

/// <summary>
/// config.xml is machine-readable: minDiskFree must round-trip identically regardless
/// of the operating system locale. Parsing it with the current culture silently turned
/// "1.5%" into the default 1% on any comma-decimal locale.
/// </summary>
public class MinDiskFreeCultureTests
{
    private static void InCulture(string culture, Action assert)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            assert();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    [InlineData("")]
    public void Parse_HandlesFractionalValues_InAnyCulture(string culture)
    {
        InCulture(culture, () =>
        {
            Assert.Equal(1.5, FolderMinDiskFree.Parse("1.5%").Value);
            Assert.Equal(0.5, FolderMinDiskFree.Parse("0.5GB").Value);
            Assert.Equal(99.9, FolderMinDiskFree.Parse("99.9%").Value);
        });
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void ToString_WritesInvariantSeparator(string culture)
    {
        InCulture(culture, () =>
        {
            var value = new FolderMinDiskFree { Value = 1.5, Unit = "%" };
            Assert.Equal("1.5%", value.ToString());

            var xmlValue = new ConfigXmlMinDiskFree { Value = 2.5, Unit = "GB" };
            Assert.Equal("2.5GB", xmlValue.ToString());
        });
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void RoundTrip_IsStable(string culture)
    {
        InCulture(culture, () =>
        {
            var parsed = FolderMinDiskFree.Parse("12.25MB");
            Assert.Equal("12.25MB", parsed.ToString());
            Assert.Equal(12.25, FolderMinDiskFree.Parse(parsed.ToString()).Value);
        });
    }
}
