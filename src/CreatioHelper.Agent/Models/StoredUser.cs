namespace CreatioHelper.Agent.Models;

public class StoredUser
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
