using System.Collections.Concurrent;

namespace CreatioHelper.Agent.Services;

/// <summary>
/// Simple in-memory rate limiter for login attempts
/// Protects against brute-force attacks by limiting attempts per IP address
/// </summary>
public class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, LoginAttemptInfo> _attempts = new();
    private readonly ILogger<LoginRateLimiter> _logger;
    
    // Configuration
    private readonly int _maxAttempts;
    private readonly TimeSpan _lockoutDuration;
    private readonly TimeSpan _attemptWindow;

    public LoginRateLimiter(ILogger<LoginRateLimiter> logger, IConfiguration configuration)
    {
        _logger = logger;
        
        // Read configuration with defaults
        var section = configuration.GetSection("RateLimiting:Login");
        _maxAttempts = section.GetValue("MaxAttempts", 5);
        _lockoutDuration = TimeSpan.FromMinutes(section.GetValue("LockoutMinutes", 15));
        _attemptWindow = TimeSpan.FromMinutes(section.GetValue("AttemptWindowMinutes", 5));
    }

    /// <summary>
    /// Check if an IP address is allowed to attempt login
    /// </summary>
    /// <param name="ipAddress">Client IP address</param>
    /// <returns>True if allowed, false if rate limited</returns>
    public bool IsAllowed(string ipAddress)
    {
        CleanupOldEntries();

        if (!_attempts.TryGetValue(ipAddress, out var info))
        {
            return true;
        }

        // Check if locked out
        if (info.LockedUntil.HasValue && info.LockedUntil.Value > DateTime.UtcNow)
        {
            _logger.LogWarning("Login attempt blocked for IP {IpAddress} - locked until {LockedUntil}", 
                ipAddress, info.LockedUntil.Value);
            return false;
        }

        // Check if within attempt window
        if (info.FirstAttempt.Add(_attemptWindow) < DateTime.UtcNow)
        {
            // Window expired, reset
            _attempts.TryRemove(ipAddress, out _);
            return true;
        }

        return info.AttemptCount < _maxAttempts;
    }

    /// <summary>
    /// Record a failed login attempt
    /// </summary>
    public void RecordFailedAttempt(string ipAddress)
    {
        _attempts.AddOrUpdate(
            ipAddress,
            _ => new LoginAttemptInfo
            {
                FirstAttempt = DateTime.UtcNow,
                AttemptCount = 1
            },
            (key, existing) =>
            {
                // Reset if window expired
                if (existing.FirstAttempt.Add(_attemptWindow) < DateTime.UtcNow)
                {
                    return new LoginAttemptInfo
                    {
                        FirstAttempt = DateTime.UtcNow,
                        AttemptCount = 1
                    };
                }

                // Create new instance with incremented count (immutable update for thread safety)
                var newCount = existing.AttemptCount + 1;
                DateTime? lockedUntil = existing.LockedUntil;

                // Lock out if max attempts exceeded
                if (newCount >= _maxAttempts && !lockedUntil.HasValue)
                {
                    lockedUntil = DateTime.UtcNow.Add(_lockoutDuration);
                    _logger.LogWarning("IP {IpAddress} locked out for {Duration} after {Attempts} failed attempts",
                        key, _lockoutDuration, newCount);
                }

                return new LoginAttemptInfo
                {
                    FirstAttempt = existing.FirstAttempt,
                    AttemptCount = newCount,
                    LockedUntil = lockedUntil
                };
            });
    }

    /// <summary>
    /// Record a successful login (clears attempt history)
    /// </summary>
    public void RecordSuccessfulLogin(string ipAddress)
    {
        _attempts.TryRemove(ipAddress, out _);
    }

    /// <summary>
    /// Get remaining lockout time for an IP address
    /// </summary>
    public TimeSpan? GetRemainingLockoutTime(string ipAddress)
    {
        if (_attempts.TryGetValue(ipAddress, out var info) && 
            info.LockedUntil.HasValue && 
            info.LockedUntil.Value > DateTime.UtcNow)
        {
            return info.LockedUntil.Value - DateTime.UtcNow;
        }
        return null;
    }

    private void CleanupOldEntries()
    {
        var cutoff = DateTime.UtcNow.Add(-_lockoutDuration).Add(-_attemptWindow);
        
        foreach (var kvp in _attempts)
        {
            if (kvp.Value.FirstAttempt < cutoff && 
                (!kvp.Value.LockedUntil.HasValue || kvp.Value.LockedUntil.Value < DateTime.UtcNow))
            {
                _attempts.TryRemove(kvp.Key, out _);
            }
        }
    }

    private class LoginAttemptInfo
    {
        public DateTime FirstAttempt { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? LockedUntil { get; set; }
    }
}
