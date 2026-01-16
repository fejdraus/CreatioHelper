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
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "CreatioHelper.Agent";
    public string Audience { get; set; } = "CreatioHelper.Client";
    public int ExpirationHours { get; set; } = 24;
}

public class SwaggerAuthSettings
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
}