using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CreatioHelper.Agent.Configuration;
using CreatioHelper.Agent.Models;
using CreatioHelper.Agent.Services;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Security;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly JwtSettings _jwtSettings;
    private readonly ILogger<AuthController> _logger;
    private readonly LoginRateLimiter _rateLimiter;
    private readonly IOptionsMonitor<SyncConfiguration> _syncConfig;
    private readonly ILdapAuthService _ldapAuthService;
    private readonly IUserStore _userStore;

    public AuthController(
        IOptions<JwtSettings> jwtSettings,
        ILogger<AuthController> logger,
        LoginRateLimiter rateLimiter,
        IOptionsMonitor<SyncConfiguration> syncConfig,
        ILdapAuthService ldapAuthService,
        IUserStore userStore)
    {
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
        _rateLimiter = rateLimiter;
        _syncConfig = syncConfig;
        _ldapAuthService = ldapAuthService;
        _userStore = userStore;
    }

    [HttpPost("token")]
    [AllowAnonymous]
    public async Task<IActionResult> GetToken([FromBody] LoginRequest request)
    {
        try
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (!_rateLimiter.IsAllowed(clientIp))
            {
                var remainingLockout = _rateLimiter.GetRemainingLockoutTime(clientIp);
                _logger.LogWarning("Rate limited login attempt from IP: {IpAddress}", clientIp);
                return StatusCode(429, new
                {
                    message = "Too many failed login attempts. Please try again later.",
                    retryAfterSeconds = remainingLockout?.TotalSeconds ?? 900
                });
            }

            var authMode = _syncConfig.CurrentValue.AuthMode;
            bool authenticated = false;
            string username = request.Username;
            string role = "user";

            if (string.Equals(authMode, "ldap", StringComparison.OrdinalIgnoreCase))
            {
                authenticated = await _ldapAuthService.AuthenticateAsync(request.Username, request.Password);
                if (authenticated)
                {
                    var storedUser = await _userStore.GetUserAsync(request.Username);
                    role = storedUser?.Role ?? "admin";
                }
            }
            else
            {
                // Authenticate via user store (bcrypt)
                authenticated = await _userStore.ValidatePasswordAsync(request.Username, request.Password);
                if (authenticated)
                {
                    var storedUser = await _userStore.GetUserAsync(request.Username);
                    role = storedUser?.Role ?? "user";
                }
            }

            if (authenticated)
            {
                _rateLimiter.RecordSuccessfulLogin(clientIp);
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.ASCII.GetBytes(_jwtSettings.Secret);
                var expires = DateTime.UtcNow.AddHours(_jwtSettings.ExpirationHours);

                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(new[]
                    {
                        new Claim("username", username),
                        new Claim("role", role),
                        new Claim(ClaimTypes.Name, username),
                        new Claim(ClaimTypes.Role, role)
                    }),
                    Expires = expires,
                    Issuer = _jwtSettings.Issuer,
                    Audience = _jwtSettings.Audience,
                    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                };

                var token = tokenHandler.CreateToken(tokenDescriptor);
                var tokenString = tokenHandler.WriteToken(token);

                _logger.LogInformation("JWT token generated for user: {Username} with role: {Role} (authMode: {AuthMode})", username, role, authMode);

                return Ok(new TokenResponse
                {
                    Token = tokenString,
                    ExpiresAt = expires,
                    TokenType = "Bearer"
                });
            }

            _rateLimiter.RecordFailedAttempt(clientIp);
            _logger.LogWarning("Failed login attempt for user: {Username} from IP: {IpAddress} (authMode: {AuthMode})", request.Username, clientIp, authMode);
            return Unauthorized(new { message = "Invalid credentials" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating JWT token");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    [HttpGet("validate")]
    [Authorize]
    public IActionResult ValidateToken()
    {
        var username = User.FindFirst("username")?.Value ?? User.Identity?.Name;
        var role = User.FindFirst("role")?.Value;

        return Ok(new
        {
            valid = true,
            username,
            role,
            claims = User.Claims.Select(c => new { c.Type, c.Value })
        });
    }

    #region User Management CRUD

    [HttpGet("users")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _userStore.GetAllUsersAsync();
        var result = users.Select(u => new UserResponse
        {
            Username = u.Username,
            Role = u.Role,
            CreatedAt = u.CreatedAt,
            UpdatedAt = u.UpdatedAt
        });
        return Ok(result);
    }

    [HttpPost("users")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(new { message = "Username is required." });
        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Password is required." });
        if (string.IsNullOrWhiteSpace(request.Role))
            return BadRequest(new { message = "Role is required." });

        try
        {
            var user = await _userStore.CreateUserAsync(request.Username, request.Password, request.Role);
            return Ok(new UserResponse
            {
                Username = user.Username,
                Role = user.Role,
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("users/{username}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> UpdateUser(string username, [FromBody] UpdateUserRequest request)
    {
        try
        {
            var user = await _userStore.UpdateUserAsync(username, request.Role, request.Password);
            return Ok(new UserResponse
            {
                Username = user.Username,
                Role = user.Role,
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = $"User '{username}' not found." });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("users/{username}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> DeleteUser(string username)
    {
        try
        {
            var deleted = await _userStore.DeleteUserAsync(username);
            if (!deleted)
                return NotFound(new { message = $"User '{username}' not found." });

            return Ok(new { message = $"User '{username}' deleted." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("users/{username}/password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(string username, [FromBody] ChangePasswordRequest request)
    {
        var currentUsername = User.FindFirst("username")?.Value ?? User.Identity?.Name;
        var currentRole = User.FindFirst("role")?.Value;
        var isSelf = string.Equals(currentUsername, username, StringComparison.OrdinalIgnoreCase);
        var isAdmin = string.Equals(currentRole, "admin", StringComparison.OrdinalIgnoreCase);

        if (!isSelf && !isAdmin)
            return Forbid();

        // If changing own password, require current password
        if (isSelf && !isAdmin)
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword))
                return BadRequest(new { message = "Current password is required." });

            var valid = await _userStore.ValidatePasswordAsync(username, request.CurrentPassword);
            if (!valid)
                return BadRequest(new { message = "Current password is incorrect." });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { message = "New password is required." });

        try
        {
            await _userStore.ChangePasswordAsync(username, request.NewPassword);
            return Ok(new { message = "Password changed successfully." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = $"User '{username}' not found." });
        }
    }

    #endregion
}

#region DTOs

public class LoginRequest
{
    public required string Username { get; set; }
    public required string Password { get; set; }
}

public class TokenResponse
{
    public required string Token { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
}

public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
}

public class UpdateUserRequest
{
    public string? Role { get; set; }
    public string? Password { get; set; }
}

public class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = string.Empty;
}

public class UserResponse
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

#endregion
