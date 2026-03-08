using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Infrastructure.Services.Sync.Security;

/// <summary>
/// LDAP authentication service compatible with Syncthing's LDAP auth implementation.
/// Supports plain, TLS, and StartTLS transports with DN template binding and optional search verification.
/// </summary>
public interface ILdapAuthService
{
    Task<bool> AuthenticateAsync(string username, string password);
}

public class LdapAuthService : ILdapAuthService
{
    private readonly IOptionsMonitor<SyncConfiguration> _config;
    private readonly ILogger<LdapAuthService> _logger;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);

    public LdapAuthService(IOptionsMonitor<SyncConfiguration> config, ILogger<LdapAuthService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task<bool> AuthenticateAsync(string username, string password)
    {
        var cfg = _config.CurrentValue;

        if (string.IsNullOrWhiteSpace(cfg.LdapAddress))
        {
            _logger.LogError("LDAP address is not configured");
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Task.FromResult(false);
        }

        try
        {
            var escapedUsername = EscapeForLdapDN(username);
            var bindDN = cfg.LdapBindDN.Replace("%s", escapedUsername);

            var connection = CreateConnection(cfg);

            // Bind with user credentials
            connection.Bind(new NetworkCredential(bindDN, password));

            // Optional: verify user exists via search
            if (!string.IsNullOrWhiteSpace(cfg.LdapSearchBaseDN) && !string.IsNullOrWhiteSpace(cfg.LdapSearchFilter))
            {
                var filterUsername = EscapeForLdapFilter(username);
                var filter = cfg.LdapSearchFilter.Replace("%s", filterUsername);

                var searchRequest = new SearchRequest(
                    cfg.LdapSearchBaseDN,
                    filter,
                    SearchScope.Subtree,
                    null);
                searchRequest.SizeLimit = 2;

                var searchResponse = (SearchResponse)connection.SendRequest(searchRequest);

                if (searchResponse.Entries.Count != 1)
                {
                    _logger.LogWarning("LDAP search returned {Count} entries for user {Username}, expected exactly 1",
                        searchResponse.Entries.Count, username);
                    connection.Dispose();
                    return Task.FromResult(false);
                }
            }

            connection.Dispose();
            _logger.LogInformation("LDAP authentication successful for user {Username}", username);
            return Task.FromResult(true);
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "LDAP authentication failed for user {Username}: {Message}", username, ex.Message);
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during LDAP authentication for user {Username}", username);
            return Task.FromResult(false);
        }
    }

    private LdapConnection CreateConnection(SyncConfiguration cfg)
    {
        var uri = new Uri(cfg.LdapAddress);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : (cfg.LdapTransport == "tls" ? 636 : 389);

        var identifier = new LdapDirectoryIdentifier(host, port);
        var connection = new LdapConnection(identifier)
        {
            Timeout = ConnectionTimeout
        };

        connection.SessionOptions.ProtocolVersion = 3;

        switch (cfg.LdapTransport.ToLowerInvariant())
        {
            case "tls":
                connection.SessionOptions.SecureSocketLayer = true;
                break;
            case "starttls":
                connection.SessionOptions.StartTransportLayerSecurity(null);
                break;
            // "plain" - no TLS
        }

        if (cfg.LdapInsecureSkipVerify)
        {
            connection.SessionOptions.VerifyServerCertificate = (conn, cert) => true;
        }

        return connection;
    }

    /// <summary>
    /// Escapes special characters in a string for use in an LDAP DN.
    /// Port of Syncthing's ldapEscapeForDN function.
    /// </summary>
    public static string EscapeForLdapDN(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case ',':
                case '+':
                case '"':
                case '\\':
                case '<':
                case '>':
                case ';':
                    sb.Append('\\');
                    sb.Append(c);
                    break;
                case '#':
                    if (i == 0)
                    {
                        sb.Append('\\');
                    }
                    sb.Append(c);
                    break;
                case ' ':
                    if (i == 0 || i == value.Length - 1)
                    {
                        sb.Append('\\');
                    }
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escapes special characters in a string for use in an LDAP search filter.
    /// Port of Syncthing's ldapEscapeForFilter function (RFC 4515).
    /// </summary>
    public static string EscapeForLdapFilter(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\5c");
                    break;
                case '*':
                    sb.Append("\\2a");
                    break;
                case '(':
                    sb.Append("\\28");
                    break;
                case ')':
                    sb.Append("\\29");
                    break;
                case '\0':
                    sb.Append("\\00");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
