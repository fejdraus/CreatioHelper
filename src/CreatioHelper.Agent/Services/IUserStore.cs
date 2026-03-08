using CreatioHelper.Agent.Models;

namespace CreatioHelper.Agent.Services;

public interface IUserStore
{
    Task<IReadOnlyList<StoredUser>> GetAllUsersAsync();
    Task<StoredUser?> GetUserAsync(string username);
    Task<bool> ValidatePasswordAsync(string username, string password);
    Task<StoredUser> CreateUserAsync(string username, string password, string role);
    Task<StoredUser> UpdateUserAsync(string username, string? newRole, string? newPassword);
    Task<bool> DeleteUserAsync(string username);
    Task ChangePasswordAsync(string username, string newPassword);
}
