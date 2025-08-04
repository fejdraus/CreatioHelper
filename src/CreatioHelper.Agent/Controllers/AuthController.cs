using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CreatioHelper.Agent.Configuration;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthenticationSettings _authSettings;
    private readonly JwtSettings _jwtSettings;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IOptions<AuthenticationSettings> authSettings,
        IOptions<JwtSettings> jwtSettings,
        ILogger<AuthController> logger)
    {
        _authSettings = authSettings.Value;
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Get JWT token for API authentication
    /// </summary>
    /// <param name="request">Login credentials</param>
    /// <returns>JWT token</returns>
    [HttpPost("token")]
    [AllowAnonymous]
    public IActionResult GetToken([FromBody] LoginRequest request)
    {
        try
        {
            // Validate user credentials from configuration
            var user = _authSettings.Users.FirstOrDefault(u => 
                u.Username == request.Username && u.Password == request.Password);
            
            if (user != null)
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.ASCII.GetBytes(_jwtSettings.Secret);
                var expires = DateTime.UtcNow.AddHours(_jwtSettings.ExpirationHours);
                
                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(new[] 
                    {
                        new Claim("username", user.Username),
                        new Claim("role", user.Role),
                        new Claim(ClaimTypes.Name, user.Username),
                        new Claim(ClaimTypes.Role, user.Role)
                    }),
                    Expires = expires,
                    Issuer = _jwtSettings.Issuer,
                    Audience = _jwtSettings.Audience,
                    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                };
                
                var token = tokenHandler.CreateToken(tokenDescriptor);
                var tokenString = tokenHandler.WriteToken(token);
                
                _logger.LogInformation("JWT token generated for user: {Username} with role: {Role}", user.Username, user.Role);
                
                return Ok(new TokenResponse 
                { 
                    Token = tokenString,
                    ExpiresAt = expires,
                    TokenType = "Bearer"
                });
            }
            
            _logger.LogWarning("Failed login attempt for user: {Username}", request.Username);
            return Unauthorized(new { message = "Invalid credentials" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating JWT token");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Validate current token (useful for testing)
    /// </summary>
    /// <returns>Token validation result</returns>
    [HttpGet("validate")]
    [Authorize]
    public IActionResult ValidateToken()
    {
        var username = User.FindFirst("username")?.Value ?? User.Identity?.Name;
        var role = User.FindFirst("role")?.Value;
        
        return Ok(new 
        { 
            valid = true, 
            username = username,
            role = role,
            claims = User.Claims.Select(c => new { c.Type, c.Value })
        });
    }
}

public class LoginRequest
{
    /// <summary>
    /// Username for authentication
    /// </summary>
    /// <example>admin</example>
    public required string Username { get; set; }
    
    /// <summary>
    /// Password for authentication
    /// </summary>
    /// <example>admin123</example>
    public required string Password { get; set; }
}

public class TokenResponse
{
    /// <summary>
    /// JWT access token
    /// </summary>
    public required string Token { get; set; }
    
    /// <summary>
    /// Token expiration date and time
    /// </summary>
    public DateTime ExpiresAt { get; set; }
    
    /// <summary>
    /// Token type (always "Bearer")
    /// </summary>
    public string TokenType { get; set; } = "Bearer";
}