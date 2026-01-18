using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Security;

/// <summary>
/// API token information
/// </summary>
public record ApiToken
{
    /// <summary>
    /// Unique token identifier
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Token name/description
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Token hash (never store the actual token)
    /// </summary>
    public string TokenHash { get; init; } = string.Empty;

    /// <summary>
    /// When the token was created
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// When the token expires (null = never)
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Last time the token was used
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Allowed scopes/permissions
    /// </summary>
    public HashSet<string> Scopes { get; init; } = new();

    /// <summary>
    /// Whether the token is enabled
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Allowed IP addresses (empty = all)
    /// </summary>
    public HashSet<string> AllowedIps { get; init; } = new();
}

/// <summary>
/// Token validation result
/// </summary>
public record TokenValidationResult
{
    public bool IsValid { get; init; }
    public ApiToken? Token { get; init; }
    public string? Error { get; init; }

    public static TokenValidationResult Valid(ApiToken token) => new()
    {
        IsValid = true,
        Token = token
    };

    public static TokenValidationResult Invalid(string error) => new()
    {
        IsValid = false,
        Error = error
    };
}

/// <summary>
/// API scopes for token permissions
/// </summary>
public static class ApiScopes
{
    public const string ReadConfig = "config:read";
    public const string WriteConfig = "config:write";
    public const string ReadStatus = "status:read";
    public const string ReadEvents = "events:read";
    public const string ReadSystem = "system:read";
    public const string WriteSystem = "system:write";
    public const string ReadDb = "db:read";
    public const string WriteDb = "db:write";
    public const string Admin = "admin";

    public static readonly string[] All = new[]
    {
        ReadConfig, WriteConfig, ReadStatus, ReadEvents,
        ReadSystem, WriteSystem, ReadDb, WriteDb, Admin
    };

    public static readonly string[] ReadOnly = new[]
    {
        ReadConfig, ReadStatus, ReadEvents, ReadSystem, ReadDb
    };

    public static readonly string[] FullAccess = All;
}

/// <summary>
/// Provides API token authentication for REST API (based on Syncthing api_auth.go)
/// </summary>
public interface IApiTokenAuthService
{
    /// <summary>
    /// Generate a new API token
    /// </summary>
    (ApiToken Token, string RawToken) GenerateToken(string name, IEnumerable<string>? scopes = null, TimeSpan? expiry = null);

    /// <summary>
    /// Validate a token and check scope
    /// </summary>
    Task<TokenValidationResult> ValidateTokenAsync(string token, string? requiredScope = null, string? clientIp = null, CancellationToken ct = default);

    /// <summary>
    /// Revoke a token
    /// </summary>
    Task<bool> RevokeTokenAsync(string tokenId, CancellationToken ct = default);

    /// <summary>
    /// List all tokens (without secrets)
    /// </summary>
    Task<IReadOnlyList<ApiToken>> ListTokensAsync(CancellationToken ct = default);

    /// <summary>
    /// Get a specific token by ID
    /// </summary>
    Task<ApiToken?> GetTokenAsync(string tokenId, CancellationToken ct = default);

    /// <summary>
    /// Update token (enable/disable, update scopes, etc.)
    /// </summary>
    Task<bool> UpdateTokenAsync(ApiToken token, CancellationToken ct = default);

    /// <summary>
    /// Check if a scope grants access to a required scope
    /// </summary>
    bool ScopeGrantsAccess(IEnumerable<string> grantedScopes, string requiredScope);
}

/// <summary>
/// Implementation of API token authentication (based on Syncthing api_auth.go)
/// </summary>
public class ApiTokenAuthService : IApiTokenAuthService
{
    private readonly ILogger<ApiTokenAuthService> _logger;
    private readonly Dictionary<string, ApiToken> _tokens = new();
    private readonly object _lock = new();
    private readonly string _storagePath;

    // Token format: prefix-random (e.g., "st-abc123def456...")
    private const string TokenPrefix = "st-";
    private const int TokenLength = 32;

    public ApiTokenAuthService(ILogger<ApiTokenAuthService> logger, string? storagePath = null)
    {
        _logger = logger;
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CreatioHelper", "tokens.json");

        LoadTokens();
    }

    public (ApiToken Token, string RawToken) GenerateToken(string name, IEnumerable<string>? scopes = null, TimeSpan? expiry = null)
    {
        // Generate secure random token
        var randomBytes = new byte[TokenLength];
        RandomNumberGenerator.Fill(randomBytes);
        var rawToken = TokenPrefix + Convert.ToHexString(randomBytes).ToLowerInvariant();

        // Hash the token for storage
        var tokenHash = HashToken(rawToken);

        var token = new ApiToken
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null,
            Scopes = new HashSet<string>(scopes ?? ApiScopes.ReadOnly),
            Enabled = true
        };

        lock (_lock)
        {
            _tokens[token.Id] = token;
        }

        SaveTokens();

        _logger.LogInformation("Generated new API token: {TokenId} ({Name})", token.Id, name);
        return (token, rawToken);
    }

    public Task<TokenValidationResult> ValidateTokenAsync(string token, string? requiredScope = null,
        string? clientIp = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(TokenValidationResult.Invalid("Token is required"));
        }

        // Check token format
        if (!token.StartsWith(TokenPrefix))
        {
            return Task.FromResult(TokenValidationResult.Invalid("Invalid token format"));
        }

        var tokenHash = HashToken(token);

        ApiToken? matchedToken = null;
        lock (_lock)
        {
            matchedToken = _tokens.Values.FirstOrDefault(t => t.TokenHash == tokenHash);
        }

        if (matchedToken == null)
        {
            _logger.LogWarning("Token not found");
            return Task.FromResult(TokenValidationResult.Invalid("Token not found"));
        }

        // Check if token is enabled
        if (!matchedToken.Enabled)
        {
            _logger.LogWarning("Token is disabled: {TokenId}", matchedToken.Id);
            return Task.FromResult(TokenValidationResult.Invalid("Token is disabled"));
        }

        // Check expiry
        if (matchedToken.ExpiresAt.HasValue && matchedToken.ExpiresAt.Value < DateTime.UtcNow)
        {
            _logger.LogWarning("Token has expired: {TokenId}", matchedToken.Id);
            return Task.FromResult(TokenValidationResult.Invalid("Token has expired"));
        }

        // Check IP restrictions
        if (matchedToken.AllowedIps.Count > 0 && !string.IsNullOrEmpty(clientIp))
        {
            if (!matchedToken.AllowedIps.Contains(clientIp))
            {
                _logger.LogWarning("Token used from unauthorized IP: {TokenId}, IP: {Ip}", matchedToken.Id, clientIp);
                return Task.FromResult(TokenValidationResult.Invalid("Unauthorized IP address"));
            }
        }

        // Check required scope
        if (!string.IsNullOrEmpty(requiredScope))
        {
            if (!ScopeGrantsAccess(matchedToken.Scopes, requiredScope))
            {
                _logger.LogWarning("Token lacks required scope: {TokenId}, required: {Scope}",
                    matchedToken.Id, requiredScope);
                return Task.FromResult(TokenValidationResult.Invalid($"Token lacks required scope: {requiredScope}"));
            }
        }

        // Update last used
        matchedToken.LastUsedAt = DateTime.UtcNow;
        SaveTokens();

        _logger.LogDebug("Token validated: {TokenId}", matchedToken.Id);
        return Task.FromResult(TokenValidationResult.Valid(matchedToken));
    }

    public Task<bool> RevokeTokenAsync(string tokenId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_tokens.Remove(tokenId))
            {
                SaveTokens();
                _logger.LogInformation("Revoked API token: {TokenId}", tokenId);
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<ApiToken>> ListTokensAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ApiToken>>(_tokens.Values.ToList());
        }
    }

    public Task<ApiToken?> GetTokenAsync(string tokenId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _tokens.TryGetValue(tokenId, out var token);
            return Task.FromResult(token);
        }
    }

    public Task<bool> UpdateTokenAsync(ApiToken token, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_tokens.ContainsKey(token.Id))
                return Task.FromResult(false);

            _tokens[token.Id] = token;
            SaveTokens();
            _logger.LogInformation("Updated API token: {TokenId}", token.Id);
            return Task.FromResult(true);
        }
    }

    public bool ScopeGrantsAccess(IEnumerable<string> grantedScopes, string requiredScope)
    {
        var scopes = grantedScopes.ToHashSet();

        // Admin grants all access
        if (scopes.Contains(ApiScopes.Admin))
            return true;

        // Direct match
        if (scopes.Contains(requiredScope))
            return true;

        // Check for wildcard scopes (e.g., "config:*" grants "config:read")
        var parts = requiredScope.Split(':');
        if (parts.Length == 2)
        {
            var wildcardScope = $"{parts[0]}:*";
            if (scopes.Contains(wildcardScope))
                return true;
        }

        return false;
    }

    private string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void LoadTokens()
    {
        try
        {
            if (!File.Exists(_storagePath))
                return;

            var json = File.ReadAllText(_storagePath);
            var tokens = JsonSerializer.Deserialize<List<ApiToken>>(json);

            if (tokens != null)
            {
                lock (_lock)
                {
                    foreach (var token in tokens)
                    {
                        _tokens[token.Id] = token;
                    }
                }
            }

            _logger.LogDebug("Loaded {Count} API tokens", _tokens.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading API tokens");
        }
    }

    private void SaveTokens()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<ApiToken> tokens;
            lock (_lock)
            {
                tokens = _tokens.Values.ToList();
            }

            var json = JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving API tokens");
        }
    }
}

/// <summary>
/// Middleware helper for API token authentication
/// </summary>
public static class ApiTokenAuthMiddleware
{
    /// <summary>
    /// Extract token from Authorization header
    /// </summary>
    public static string? ExtractToken(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
            return null;

        // Support "Bearer TOKEN" and "X-API-Key TOKEN" formats
        if (authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader.Substring(7).Trim();
        }

        if (authorizationHeader.StartsWith("X-API-Key ", StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader.Substring(10).Trim();
        }

        // Also accept raw token
        if (authorizationHeader.StartsWith("st-"))
        {
            return authorizationHeader.Trim();
        }

        return null;
    }
}
