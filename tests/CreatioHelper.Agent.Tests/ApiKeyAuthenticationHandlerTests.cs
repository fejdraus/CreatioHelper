using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using CreatioHelper.Agent.Authentication;
using CreatioHelper.Agent.Authorization;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CreatioHelper.Agent.Tests;

public class ApiKeyAuthenticationHandlerTests
{
    private const string ConfiguredKey = "a-key-nobody-else-knows";

    private static async Task<AuthenticateResult> AuthenticateAsync(string? presentedKey, string configuredKey = ConfiguredKey)
    {
        var syncConfiguration = new SyncConfiguration("DEVICE", "device");
        syncConfiguration.GuiApiKey = configuredKey;

        var options = new OptionsMonitorStub();
        var handler = new ApiKeyAuthenticationHandler(
            options,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            syncConfiguration);

        var context = new DefaultHttpContext();
        if (presentedKey != null)
        {
            context.Request.Headers[ApiKeyAuthenticationHandler.HeaderName] = presentedKey;
        }

        var scheme = new AuthenticationScheme(
            ApiKeyAuthenticationHandler.SchemeName,
            ApiKeyAuthenticationHandler.SchemeName,
            typeof(ApiKeyAuthenticationHandler));

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task CorrectKeyIsAccepted()
    {
        var result = await AuthenticateAsync(ConfiguredKey);

        Assert.True(result.Succeeded);
        Assert.Equal("api-key", result.Principal!.Identity!.Name);
    }

    [Fact]
    public async Task AcceptedKeyGrantsReadOnlyAndNothingMore()
    {
        var result = await AuthenticateAsync(ConfiguredKey);

        Assert.True(result.Principal!.IsInRole(Roles.ReadOnly));
        Assert.False(result.Principal.IsInRole(Roles.Admin));
        Assert.False(result.Principal.IsInRole(Roles.User));
        Assert.False(result.Principal.IsInRole(Roles.Monitor));
    }

    [Fact]
    public async Task WrongKeyIsRejected()
    {
        var result = await AuthenticateAsync("not-the-key");

        Assert.False(result.Succeeded);
        Assert.False(result.None);
    }

    [Fact]
    public async Task KeyDifferingOnlyInCaseIsRejected()
    {
        var result = await AuthenticateAsync(ConfiguredKey.ToUpperInvariant());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task PrefixOfTheKeyIsRejected()
    {
        var result = await AuthenticateAsync(ConfiguredKey[..5]);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task MissingHeaderDefersToTheOtherScheme()
    {
        var result = await AuthenticateAsync(null);

        Assert.True(result.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task EmptyHeaderIsRejected()
    {
        var result = await AuthenticateAsync(string.Empty);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task NothingIsAcceptedWhenNoKeyIsConfigured()
    {
        var result = await AuthenticateAsync("anything", configuredKey: string.Empty);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DefaultKeyStillWorksSoExistingSetupsKeepRunning()
    {
        var result = await AuthenticateAsync(
            ApiKeyAuthenticationHandler.WellKnownDefaultKey,
            ApiKeyAuthenticationHandler.WellKnownDefaultKey);

        Assert.True(result.Succeeded);
    }

    private class OptionsMonitorStub : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        private readonly AuthenticationSchemeOptions _options = new();

        public AuthenticationSchemeOptions CurrentValue => _options;

        public AuthenticationSchemeOptions Get(string? name) => _options;

        public IDisposable? OnChange(System.Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
