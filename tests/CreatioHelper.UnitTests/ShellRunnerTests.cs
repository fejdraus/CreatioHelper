using System.Text.Json;
using CreatioHelper.Shared.Utils;
using Xunit;

namespace CreatioHelper.UnitTests;

public class ShellRunnerTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("O'Brien", "O'\\''Brien")]
    [InlineData("a'b'c", "a'\\''b'\\''c")]
    [InlineData("", "")]
    public void BashEscaping_ClosesAndReopensTheLiteral(string value, string expected)
    {
        Assert.Equal(expected, BashRunner.EscapeSingleQuoted(value));
    }

    [Fact]
    public void BashEscaping_DiffersFromPowerShellEscaping()
    {
        Assert.Equal("O''Brien", PowerShellRunner.EscapeSingleQuoted("O'Brien"));
        Assert.Equal("O'\\''Brien", BashRunner.EscapeSingleQuoted("O'Brien"));
    }

    [Theory]
    [InlineData("Started", "Started")]
    [InlineData("Started\r\n", "Started")]
    [InlineData("WARNING: module loaded\r\nStarted\r\n", "Started")]
    [InlineData("Started\r\n\r\n", "Started")]
    [InlineData("  Started  ", "Started")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void LastLineOf_TakesTheFinalMeaningfulLine(string? output, string? expected)
    {
        Assert.Equal(expected, ShellResult.LastLineOf(output));
    }

    [Fact]
    public void CaseInsensitiveOptionsAreASingleCachedInstance()
    {
        Assert.Same(JsonDefaults.CaseInsensitive, JsonDefaults.CaseInsensitive);
        Assert.True(JsonDefaults.CaseInsensitive.PropertyNameCaseInsensitive);
    }

    [Fact]
    public void CaseInsensitiveOptionsDeserialiseMismatchedCasing()
    {
        var parsed = JsonSerializer.Deserialize<Probe>("""{"name":"iis","STATE":"Started"}""", JsonDefaults.CaseInsensitive);

        Assert.NotNull(parsed);
        Assert.Equal("iis", parsed!.Name);
        Assert.Equal("Started", parsed.State);
    }

    private sealed class Probe
    {
        public string Name { get; set; } = "";
        public string State { get; set; } = "";
    }
}
