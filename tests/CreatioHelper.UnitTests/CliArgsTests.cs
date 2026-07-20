using CreatioHelper.Cli;
using Xunit;

namespace CreatioHelper.UnitTests;

public class CliArgsTests
{
    [Fact]
    public void ParsesEqualsSyntax()
    {
        var c = CliArgs.Parse(new[] { "--port=5275" });

        Assert.Equal("5275", c.Get("port"));
        Assert.DoesNotContain("port=5275", c.Flags);
    }

    [Fact]
    public void ParsesSpaceSeparatedValue()
    {
        var c = CliArgs.Parse(new[] { "--name", "value" });

        Assert.Equal("value", c.Get("name"));
    }

    [Fact]
    public void EqualsSyntaxCarriesAValueThatLooksLikeAnOption()
    {
        var c = CliArgs.Parse(new[] { "--msg=--hello" });

        Assert.Equal("--hello", c.Get("msg"));
        Assert.False(c.HasFlag("msg"));
    }

    [Fact]
    public void EqualsSyntaxSplitsOnTheFirstSign()
    {
        var c = CliArgs.Parse(new[] { "--filter=a=b" });

        Assert.Equal("a=b", c.Get("filter"));
    }

    [Fact]
    public void EqualsSyntaxAllowsAnEmptyValue()
    {
        var c = CliArgs.Parse(new[] { "--label=" });

        Assert.Equal("", c.Get("label"));
        Assert.False(c.HasFlag("label"));
    }

    [Fact]
    public void KeepsNegativeNumbersAsValues()
    {
        var c = CliArgs.Parse(new[] { "--offset", "-5" });

        Assert.Equal("-5", c.Get("offset"));
    }

    [Fact]
    public void TreatsTwoOptionsWithoutValuesAsFlags()
    {
        var c = CliArgs.Parse(new[] { "--verbose", "--force" });

        Assert.True(c.HasFlag("verbose"));
        Assert.True(c.HasFlag("force"));
        Assert.Null(c.Get("verbose"));
    }

    [Fact]
    public void CollectsRepeatedOptionsInBothForms()
    {
        var spaceForm = CliArgs.Parse(new[] { "--tag", "x", "--tag", "y" });
        var equalsForm = CliArgs.Parse(new[] { "--tag=x", "--tag=y" });

        Assert.Equal(new[] { "x", "y" }, spaceForm.GetAll("tag"));
        Assert.Equal(new[] { "x", "y" }, equalsForm.GetAll("tag"));
        Assert.Equal("y", spaceForm.Get("tag"));
    }

    [Fact]
    public void ReadsCommandAndSubCommand()
    {
        var c = CliArgs.Parse(new[] { "deploy", "start", "--site=A" });

        Assert.Equal("deploy", c.Command);
        Assert.Equal("start", c.SubCommand);
        Assert.Equal("A", c.Get("site"));
    }

    [Fact]
    public void OptionKeysAreCaseInsensitive()
    {
        var c = CliArgs.Parse(new[] { "--Site=A" });

        Assert.Equal("A", c.Get("site"));
    }
}
