namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Service for CSRF (Cross-Site Request Forgery) token protection.
/// Based on Syncthing's CSRF protection from lib/api/api.go
///
/// Key behaviors:
/// - Generate secure CSRF tokens
/// - Validate tokens on state-changing requests (POST, PUT, DELETE, PATCH)
/// - Token can be in header (X-CSRF-Token) or form field (csrf_token)
/// - Tokens are tied to sessions and have configurable lifetime
/// </summary>
public interface ICsrfProtectionService
{
    /// <summary>
    /// Generate a new CSRF token for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier (can be empty for stateless mode)</param>
    /// <returns>The generated token</returns>
    string GenerateToken(string? sessionId = null);

    /// <summary>
    /// Validate a CSRF token.
    /// </summary>
    /// <param name="token">Token to validate</param>
    /// <param name="sessionId">Session identifier (can be empty for stateless mode)</param>
    /// <returns>True if token is valid</returns>
    bool ValidateToken(string token, string? sessionId = null);

    /// <summary>
    /// Check if a request method requires CSRF validation.
    /// </summary>
    /// <param name="method">HTTP method (GET, POST, etc.)</param>
    /// <returns>True if CSRF validation is required</returns>
    bool RequiresValidation(string method);

    /// <summary>
    /// Revoke a specific token (e.g., on logout).
    /// </summary>
    /// <param name="token">Token to revoke</param>
    void RevokeToken(string token);

    /// <summary>
    /// Revoke all tokens for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    void RevokeSessionTokens(string sessionId);

    /// <summary>
    /// Clean up expired tokens.
    /// </summary>
    /// <returns>Number of tokens cleaned up</returns>
    int CleanupExpiredTokens();

    /// <summary>
    /// Get statistics about CSRF tokens.
    /// </summary>
    CsrfStatistics GetStatistics();

    /// <summary>
    /// Get current configuration.
    /// </summary>
    CsrfConfiguration GetConfiguration();
}

/// <summary>
/// Configuration for CSRF protection.
/// </summary>
public class CsrfConfiguration
{
    /// <summary>
    /// Whether CSRF protection is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Token lifetime in minutes.
    /// </summary>
    public int TokenLifetimeMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum tokens per session.
    /// </summary>
    public int MaxTokensPerSession { get; set; } = 10;

    /// <summary>
    /// Token length in bytes (will be hex-encoded, so double this for string length).
    /// </summary>
    public int TokenLengthBytes { get; set; } = 32;

    /// <summary>
    /// Header name for CSRF token.
    /// </summary>
    public string HeaderName { get; set; } = "X-CSRF-Token";

    /// <summary>
    /// Alternative header name (Syncthing compatibility).
    /// </summary>
    public string AlternativeHeaderName { get; set; } = "X-CSRF-Token-ST";

    /// <summary>
    /// Form field name for CSRF token.
    /// </summary>
    public string FormFieldName { get; set; } = "csrf_token";

    /// <summary>
    /// Cookie name for CSRF token.
    /// </summary>
    public string CookieName { get; set; } = "CSRF-Token";

    /// <summary>
    /// HTTP methods that require CSRF validation.
    /// </summary>
    public HashSet<string> ProtectedMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "DELETE", "PATCH"
    };

    /// <summary>
    /// Paths exempt from CSRF validation (e.g., API endpoints with token auth).
    /// </summary>
    public HashSet<string> ExemptPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/", "/rest/events"
    };

    /// <summary>
    /// Whether to use double-submit cookie pattern.
    /// </summary>
    public bool UseDoubleSubmitCookie { get; set; } = true;

    /// <summary>
    /// Cleanup interval in minutes.
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 15;
}

/// <summary>
/// Statistics about CSRF tokens.
/// </summary>
public class CsrfStatistics
{
    /// <summary>
    /// Total tokens currently active.
    /// </summary>
    public int ActiveTokenCount { get; set; }

    /// <summary>
    /// Total sessions with tokens.
    /// </summary>
    public int SessionCount { get; set; }

    /// <summary>
    /// Total tokens generated.
    /// </summary>
    public long TotalTokensGenerated { get; set; }

    /// <summary>
    /// Total validation attempts.
    /// </summary>
    public long TotalValidationAttempts { get; set; }

    /// <summary>
    /// Successful validations.
    /// </summary>
    public long SuccessfulValidations { get; set; }

    /// <summary>
    /// Failed validations.
    /// </summary>
    public long FailedValidations { get; set; }

    /// <summary>
    /// Tokens expired.
    /// </summary>
    public long TokensExpired { get; set; }

    /// <summary>
    /// Tokens revoked.
    /// </summary>
    public long TokensRevoked { get; set; }

    /// <summary>
    /// Last cleanup time.
    /// </summary>
    public DateTime? LastCleanupTime { get; set; }

    /// <summary>
    /// Validation success rate.
    /// </summary>
    public double SuccessRate => TotalValidationAttempts > 0
        ? (double)SuccessfulValidations / TotalValidationAttempts * 100
        : 0;
}
