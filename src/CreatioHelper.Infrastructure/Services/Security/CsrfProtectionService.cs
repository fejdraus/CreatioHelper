using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Implementation of CSRF protection.
/// Based on Syncthing's CSRF protection from lib/api/api.go
///
/// Key behaviors:
/// - Cryptographically secure token generation
/// - Time-limited token validity
/// - Session binding for stateful mode
/// - Support for double-submit cookie pattern
/// </summary>
public class CsrfProtectionService : ICsrfProtectionService, IDisposable
{
    private readonly ILogger<CsrfProtectionService> _logger;
    private readonly CsrfConfiguration _config;
    private readonly ConcurrentDictionary<string, CsrfTokenEntry> _tokens = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _sessionTokens = new();
    private readonly Timer _cleanupTimer;
    private readonly object _statsLock = new();

    private long _totalTokensGenerated;
    private long _totalValidationAttempts;
    private long _successfulValidations;
    private long _failedValidations;
    private long _tokensExpired;
    private long _tokensRevoked;
    private DateTime? _lastCleanupTime;
    private bool _disposed;

    public CsrfProtectionService(
        ILogger<CsrfProtectionService> logger,
        CsrfConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new CsrfConfiguration();

        // Start cleanup timer
        _cleanupTimer = new Timer(
            _ => CleanupExpiredTokens(),
            null,
            TimeSpan.FromMinutes(_config.CleanupIntervalMinutes),
            TimeSpan.FromMinutes(_config.CleanupIntervalMinutes));

        _logger.LogInformation("CSRF protection service initialized (enabled: {Enabled}, lifetime: {Lifetime} min)",
            _config.Enabled, _config.TokenLifetimeMinutes);
    }

    /// <inheritdoc />
    public string GenerateToken(string? sessionId = null)
    {
        if (!_config.Enabled)
        {
            return string.Empty;
        }

        // Generate cryptographically secure token
        var tokenBytes = RandomNumberGenerator.GetBytes(_config.TokenLengthBytes);
        var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();

        var entry = new CsrfTokenEntry
        {
            Token = token,
            SessionId = sessionId ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_config.TokenLifetimeMinutes)
        };

        _tokens[token] = entry;

        // Track session tokens
        if (!string.IsNullOrEmpty(sessionId))
        {
            var sessionTokenSet = _sessionTokens.GetOrAdd(sessionId, _ => new HashSet<string>());
            lock (sessionTokenSet)
            {
                sessionTokenSet.Add(token);

                // Enforce max tokens per session
                while (sessionTokenSet.Count > _config.MaxTokensPerSession)
                {
                    var oldestToken = sessionTokenSet
                        .Where(t => _tokens.TryGetValue(t, out var e) && e != null)
                        .OrderBy(t => _tokens.TryGetValue(t, out var e) ? e.CreatedAt : DateTime.MaxValue)
                        .FirstOrDefault();

                    if (oldestToken != null)
                    {
                        sessionTokenSet.Remove(oldestToken);
                        _tokens.TryRemove(oldestToken, out _);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        Interlocked.Increment(ref _totalTokensGenerated);

        _logger.LogDebug("Generated CSRF token for session {SessionId}", sessionId ?? "(stateless)");

        return token;
    }

    /// <inheritdoc />
    public bool ValidateToken(string token, string? sessionId = null)
    {
        Interlocked.Increment(ref _totalValidationAttempts);

        if (!_config.Enabled)
        {
            Interlocked.Increment(ref _successfulValidations);
            return true;
        }

        if (string.IsNullOrEmpty(token))
        {
            Interlocked.Increment(ref _failedValidations);
            _logger.LogWarning("CSRF validation failed: empty token");
            return false;
        }

        if (!_tokens.TryGetValue(token, out var entry))
        {
            Interlocked.Increment(ref _failedValidations);
            _logger.LogWarning("CSRF validation failed: token not found");
            return false;
        }

        // Check expiration
        if (DateTime.UtcNow > entry.ExpiresAt)
        {
            Interlocked.Increment(ref _failedValidations);
            _tokens.TryRemove(token, out _);
            _logger.LogWarning("CSRF validation failed: token expired");
            return false;
        }

        // Check session binding (if both have session IDs)
        if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(entry.SessionId))
        {
            if (!string.Equals(sessionId, entry.SessionId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _failedValidations);
                _logger.LogWarning("CSRF validation failed: session mismatch");
                return false;
            }
        }

        // Use timing-safe comparison for the token
        var tokenBytes = System.Text.Encoding.UTF8.GetBytes(token);
        var entryBytes = System.Text.Encoding.UTF8.GetBytes(entry.Token);

        if (!CryptographicOperations.FixedTimeEquals(tokenBytes, entryBytes))
        {
            Interlocked.Increment(ref _failedValidations);
            _logger.LogWarning("CSRF validation failed: token mismatch");
            return false;
        }

        Interlocked.Increment(ref _successfulValidations);
        entry.LastUsedAt = DateTime.UtcNow;

        _logger.LogDebug("CSRF token validated successfully");
        return true;
    }

    /// <inheritdoc />
    public bool RequiresValidation(string method)
    {
        if (!_config.Enabled)
        {
            return false;
        }

        return _config.ProtectedMethods.Contains(method);
    }

    /// <summary>
    /// Check if a path is exempt from CSRF validation.
    /// </summary>
    public bool IsPathExempt(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return _config.ExemptPaths.Any(exempt =>
            path.StartsWith(exempt, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public void RevokeToken(string token)
    {
        if (_tokens.TryRemove(token, out var entry))
        {
            // Remove from session tracking
            if (!string.IsNullOrEmpty(entry.SessionId) &&
                _sessionTokens.TryGetValue(entry.SessionId, out var sessionTokenSet))
            {
                lock (sessionTokenSet)
                {
                    sessionTokenSet.Remove(token);
                }
            }

            Interlocked.Increment(ref _tokensRevoked);
            _logger.LogDebug("Revoked CSRF token");
        }
    }

    /// <inheritdoc />
    public void RevokeSessionTokens(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        if (_sessionTokens.TryRemove(sessionId, out var sessionTokenSet))
        {
            lock (sessionTokenSet)
            {
                foreach (var token in sessionTokenSet)
                {
                    if (_tokens.TryRemove(token, out _))
                    {
                        Interlocked.Increment(ref _tokensRevoked);
                    }
                }
            }

            _logger.LogDebug("Revoked all CSRF tokens for session {SessionId}", sessionId);
        }
    }

    /// <inheritdoc />
    public int CleanupExpiredTokens()
    {
        var now = DateTime.UtcNow;
        var expiredCount = 0;

        var expiredTokens = _tokens
            .Where(kv => kv.Value.ExpiresAt < now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var token in expiredTokens)
        {
            if (_tokens.TryRemove(token, out var entry))
            {
                // Remove from session tracking
                if (!string.IsNullOrEmpty(entry.SessionId) &&
                    _sessionTokens.TryGetValue(entry.SessionId, out var sessionTokenSet))
                {
                    lock (sessionTokenSet)
                    {
                        sessionTokenSet.Remove(token);
                    }
                }

                expiredCount++;
            }
        }

        // Clean up empty session entries
        var emptySessions = _sessionTokens
            .Where(kv =>
            {
                lock (kv.Value)
                {
                    return kv.Value.Count == 0;
                }
            })
            .Select(kv => kv.Key)
            .ToList();

        foreach (var sessionId in emptySessions)
        {
            _sessionTokens.TryRemove(sessionId, out _);
        }

        lock (_statsLock)
        {
            _tokensExpired += expiredCount;
            _lastCleanupTime = now;
        }

        if (expiredCount > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired CSRF tokens", expiredCount);
        }

        return expiredCount;
    }

    /// <inheritdoc />
    public CsrfStatistics GetStatistics()
    {
        return new CsrfStatistics
        {
            ActiveTokenCount = _tokens.Count,
            SessionCount = _sessionTokens.Count,
            TotalTokensGenerated = Interlocked.Read(ref _totalTokensGenerated),
            TotalValidationAttempts = Interlocked.Read(ref _totalValidationAttempts),
            SuccessfulValidations = Interlocked.Read(ref _successfulValidations),
            FailedValidations = Interlocked.Read(ref _failedValidations),
            TokensExpired = Interlocked.Read(ref _tokensExpired),
            TokensRevoked = Interlocked.Read(ref _tokensRevoked),
            LastCleanupTime = _lastCleanupTime
        };
    }

    /// <inheritdoc />
    public CsrfConfiguration GetConfiguration()
    {
        return _config;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Internal token entry.
    /// </summary>
    private class CsrfTokenEntry
    {
        public string Token { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
    }
}
