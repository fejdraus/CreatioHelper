namespace CreatioHelper.Agent.Configuration;

public class AuthenticationSettings
{
    public List<UserCredentials> Users { get; set; } = new();
}

public class UserCredentials
{
    public required string Username { get; set; }
    public required string Password { get; set; }
    public required string Role { get; set; }
}

public class JwtSettings
{
    public required string Secret { get; set; }
    public required string Issuer { get; set; }
    public required string Audience { get; set; }
    public int ExpirationHours { get; set; } = 24;
}

public class SwaggerAuthSettings
{
    public required string Username { get; set; }
    public required string Password { get; set; }
    public bool Enabled { get; set; } = true;
}