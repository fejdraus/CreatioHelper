using CreatioHelper.Agent.Logging;
using Xunit;

namespace CreatioHelper.Agent.Tests;

public class SensitiveQueryStringEnricherTests
{
    [Fact]
    public void Redact_RemovesSignalRAccessToken()
    {
        var result = SensitiveQueryStringEnricher.Redact("?id=YSKbiMYSRjdmYZC4D0hcrA&access_token=eyJhbGciOiJIUzI1NiJ9.payload.signature");

        Assert.Equal("?id=YSKbiMYSRjdmYZC4D0hcrA&access_token=REDACTED", result);
    }

    [Theory]
    [InlineData("?token=abc", "?token=REDACTED")]
    [InlineData("?api_key=abc", "?api_key=REDACTED")]
    [InlineData("?apikey=abc", "?apikey=REDACTED")]
    [InlineData("?password=abc", "?password=REDACTED")]
    [InlineData("?secret=abc", "?secret=REDACTED")]
    [InlineData("?refresh_token=abc", "?refresh_token=REDACTED")]
    public void Redact_RemovesEverySensitiveParameter(string queryString, string expected)
    {
        Assert.Equal(expected, SensitiveQueryStringEnricher.Redact(queryString));
    }

    [Fact]
    public void Redact_IsCaseInsensitive()
    {
        Assert.Equal("?Access_Token=REDACTED", SensitiveQueryStringEnricher.Redact("?Access_Token=abc"));
    }

    [Fact]
    public void Redact_KeepsHarmlessParameters()
    {
        const string queryString = "?negotiateVersion=1&id=abc";

        Assert.Equal(queryString, SensitiveQueryStringEnricher.Redact(queryString));
    }

    [Fact]
    public void Redact_DoesNotMatchParameterSubstrings()
    {
        const string queryString = "?access_token_hint=abc";

        Assert.Equal(queryString, SensitiveQueryStringEnricher.Redact(queryString));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("?flag")]
    public void Redact_HandlesDegenerateInput(string queryString)
    {
        Assert.Equal(queryString, SensitiveQueryStringEnricher.Redact(queryString));
    }
}
