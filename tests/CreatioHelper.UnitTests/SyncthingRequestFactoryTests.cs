using System.Linq;
using System.Net.Http;
using CreatioHelper.Services;
using Xunit;

namespace CreatioHelper.UnitTests;

public class SyncthingRequestFactoryTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8384", "http://127.0.0.1:8384/rest/system/status")]
    [InlineData("http://127.0.0.1:8384/", "http://127.0.0.1:8384/rest/system/status")]
    [InlineData("http://127.0.0.1:8384///", "http://127.0.0.1:8384/rest/system/status")]
    public void TrimsTrailingSlashesOfTheApiUrl(string apiUrl, string expected)
    {
        var factory = new SyncthingRequestFactory(apiUrl, null);

        Assert.Equal(expected, factory.BuildUrl("/rest/system/status"));
    }

    [Fact]
    public void AddsTheApiKeyHeader()
    {
        var factory = new SyncthingRequestFactory("http://127.0.0.1:8384", "secret-key");

        using var request = factory.Get("/rest/db/status?folder=abc");

        Assert.True(request.Headers.TryGetValues("X-API-Key", out var values));
        Assert.Equal("secret-key", values!.Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OmitsTheHeaderWhenNoKeyIsConfigured(string? apiKey)
    {
        var factory = new SyncthingRequestFactory("http://127.0.0.1:8384", apiKey);

        using var request = factory.Get("/rest/system/status");

        Assert.False(request.Headers.Contains("X-API-Key"));
    }

    [Fact]
    public void KeepsTheRequestedMethod()
    {
        var factory = new SyncthingRequestFactory("http://127.0.0.1:8384", "k");

        using var request = factory.Create(HttpMethod.Post, "/rest/system/restart");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:8384/rest/system/restart", request.RequestUri!.ToString());
    }

    [Fact]
    public void HandlesAnEmptyApiUrl()
    {
        var factory = new SyncthingRequestFactory("", null);

        Assert.Equal("/rest/system/status", factory.BuildUrl("/rest/system/status"));
    }
}
