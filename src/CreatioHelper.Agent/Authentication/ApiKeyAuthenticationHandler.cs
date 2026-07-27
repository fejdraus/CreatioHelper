using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CreatioHelper.Agent.Authorization;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Agent.Authentication;

/// <summary>
/// Accepts the Syncthing-style X-API-Key header so tools written against the
/// Syncthing REST API - CreatioHelper Desktop among them - can talk to the agent
/// without holding a JWT. The key grants the readonly role, which is the least
/// privilege covering every endpoint those tools read and which denies every
/// write.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-API-Key";

    /// <summary>
    /// Shipped as the default in SyncConfiguration, so it is public knowledge and
    /// must not be trusted silently.
    /// </summary>
    public const string WellKnownDefaultKey = "syncthing-compatible-key";

    private readonly SyncConfiguration _syncConfiguration;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SyncConfiguration syncConfiguration)
        : base(options, logger, encoder)
    {
        _syncConfiguration = syncConfiguration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = provided.ToString();
        var expected = _syncConfiguration.GuiApiKey;

        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("API key is not accepted"));
        }

        if (!KeysMatch(presented, expected))
        {
            Logger.LogWarning("Rejected a request carrying an invalid {Header}", HeaderName);
            return Task.FromResult(AuthenticateResult.Fail("API key is not accepted"));
        }

        if (string.Equals(expected, WellKnownDefaultKey, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "A request was authenticated with the built-in default API key. " +
                "Change <apikey> in config.xml so it is not a value anyone can guess");
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "api-key"),
                new Claim(ClaimTypes.Role, Roles.ReadOnly)
            },
            SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Hashing both sides first keeps the comparison independent of key length,
    /// which a plain fixed-time comparison of the raw bytes would still leak.
    /// </summary>
    private static bool KeysMatch(string presented, string expected)
    {
        Span<byte> presentedHash = stackalloc byte[32];
        Span<byte> expectedHash = stackalloc byte[32];

        SHA256.HashData(Encoding.UTF8.GetBytes(presented), presentedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);

        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
