using CreatioHelper.ViewModels;
using Xunit;

namespace CreatioHelper.UnitTests;

public class RedisSectionAttributeTests
{
    private static RedisSectionAttributeViewModel Numeric(string value, string recommended = "5000")
        => new() { Name = "clientSyncTimeoutMs", DisplayName = "Sync operation timeout, ms", RecommendedValue = recommended, Value = value };

    private static RedisSectionAttributeViewModel Boolean(string value, string recommended = "false")
        => new() { Name = "abortOnConnectFail", DisplayName = "Abort on connect failure", RecommendedValue = recommended, IsBoolean = true, Value = value };

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("nonsense", false)]
    public void BoolValue_ParsesRawValue(string raw, bool expected)
    {
        Assert.Equal(expected, Boolean(raw).BoolValue);
    }

    [Fact]
    public void BoolValue_WritesLowercaseLiteral()
    {
        var attribute = Boolean("false");

        attribute.BoolValue = true;

        Assert.Equal("true", attribute.Value);
    }

    [Fact]
    public void DiffersFromRecommended_IsFalse_WhenValueMatches()
    {
        Assert.False(Numeric("5000").DiffersFromRecommended);
    }

    [Fact]
    public void DiffersFromRecommended_IgnoresSurroundingWhitespaceAndCase()
    {
        Assert.False(Numeric(" 5000 ").DiffersFromRecommended);
        Assert.False(Boolean("FALSE").DiffersFromRecommended);
    }

    [Fact]
    public void DiffersFromRecommended_IsTrue_WhenValueDeviates()
    {
        Assert.True(Numeric("30000").DiffersFromRecommended);
    }

    [Fact]
    public void DiffersFromRecommended_IsFalse_WhenNoRecommendationExists()
    {
        var attribute = new RedisSectionAttributeViewModel { Name = "connectionStringName", Value = "redis" };

        Assert.False(attribute.DiffersFromRecommended);
    }

    [Fact]
    public void DiffersFromRecommended_TracksValueChanges()
    {
        var attribute = Numeric("5000");
        Assert.False(attribute.DiffersFromRecommended);

        attribute.Value = "30000";

        Assert.True(attribute.DiffersFromRecommended);
    }

    [Fact]
    public void IsText_IsInverseOfIsBoolean()
    {
        Assert.True(Numeric("5000").IsText);
        Assert.False(Boolean("false").IsText);
    }

    [Fact]
    public void RecommendedHint_ShowsTheRecommendedValue()
    {
        Assert.Equal("Recommended for a cluster: 5000", Numeric("30000").RecommendedHint);
    }

    [Fact]
    public void ShowRecommendation_IsFalse_OutsideClusterMode()
    {
        var attribute = Numeric("30000");

        Assert.True(attribute.DiffersFromRecommended);
        Assert.False(attribute.ShowRecommendation);
    }

    [Fact]
    public void ShowRecommendation_RequiresBothClusterModeAndDeviation()
    {
        var deviating = Numeric("30000");
        var matching = Numeric("5000");

        deviating.ShowRecommendations = true;
        matching.ShowRecommendations = true;

        Assert.True(deviating.ShowRecommendation);
        Assert.False(matching.ShowRecommendation);
    }

    [Fact]
    public void ShowRecommendation_TracksValueChangesInClusterMode()
    {
        var attribute = Numeric("5000");
        attribute.ShowRecommendations = true;
        Assert.False(attribute.ShowRecommendation);

        attribute.Value = "30000";

        Assert.True(attribute.ShowRecommendation);
    }
}
