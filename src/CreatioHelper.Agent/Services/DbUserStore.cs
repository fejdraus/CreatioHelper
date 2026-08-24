using System.Data.Common;
using CreatioHelper.Agent.Models;
using CreatioHelper.Infrastructure.Services.Configuration.Store;
using Dapper;

namespace CreatioHelper.Agent.Services;

public class DbUserStore : IUserStore
{
    private const int BcryptWorkFactor = 12;
    private static readonly string[] ValidRoles = ["admin", "user", "readonly", "monitor"];

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DbUserStore> _logger;

    public DbUserStore(IDbConnectionFactory connectionFactory, ILogger<DbUserStore> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private async Task<DbConnection> OpenAsync()
    {
        var connection = (DbConnection)_connectionFactory.Create();
        await connection.OpenAsync();
        return connection;
    }

    private static string NormalizeUsername(string? username) => username?.Trim() ?? string.Empty;

    private static bool IsBcryptHash(string hash) =>
        !string.IsNullOrEmpty(hash) && (hash.StartsWith("$2a$") || hash.StartsWith("$2b$") || hash.StartsWith("$2y$"));

    private const string SelectColumns =
        "username AS Username, password_hash AS PasswordHash, role AS Role, created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<IReadOnlyList<StoredUser>> GetAllUsersAsync()
    {
        await using var connection = await OpenAsync();
        var users = await connection.QueryAsync<StoredUser>($"SELECT {SelectColumns} FROM config_users");
        return users.ToList();
    }

    public async Task<StoredUser?> GetUserAsync(string username)
    {
        var normalized = NormalizeUsername(username);
        await using var connection = await OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<StoredUser>(
            $"SELECT {SelectColumns} FROM config_users WHERE LOWER(username) = LOWER(@normalized)",
            new { normalized });
    }

    public async Task<bool> ValidatePasswordAsync(string username, string password)
    {
        var user = await GetUserAsync(username);
        if (user == null)
        {
            return false;
        }

        if (!IsBcryptHash(user.PasswordHash))
        {
            return false;
        }

        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
    }

    public async Task<StoredUser> CreateUserAsync(string username, string password, string role)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }
        if (!ValidRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid role. Valid roles: {string.Join(", ", ValidRoles)}", nameof(role));
        }

        var normalized = NormalizeUsername(username);
        if (await GetUserAsync(normalized) != null)
        {
            throw new InvalidOperationException($"User '{normalized}' already exists.");
        }

        var user = new StoredUser
        {
            Username = normalized,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor),
            Role = role.ToLowerInvariant(),
            CreatedAt = DateTime.UtcNow
        };

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO config_users (username, password_hash, role, created_at, updated_at)
              VALUES (@Username, @PasswordHash, @Role, @CreatedAt, @UpdatedAt)",
            user);

        _logger.LogInformation("Created user {Username} in the database", normalized);
        return user;
    }

    public async Task ImportUserAsync(StoredUser user)
    {
        if (await GetUserAsync(user.Username) != null)
        {
            return;
        }

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO config_users (username, password_hash, role, created_at, updated_at)
              VALUES (@Username, @PasswordHash, @Role, @CreatedAt, @UpdatedAt)",
            user);

        _logger.LogInformation("Imported user {Username} into the database", user.Username);
    }

    public async Task<StoredUser> UpdateUserAsync(string username, string? newRole, string? newPassword)
    {
        if (newRole != null && !ValidRoles.Contains(newRole, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid role. Valid roles: {string.Join(", ", ValidRoles)}", nameof(newRole));
        }

        var user = await GetUserAsync(username)
            ?? throw new InvalidOperationException($"User '{username}' not found.");

        if (newRole != null)
        {
            user.Role = newRole.ToLowerInvariant();
        }
        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, BcryptWorkFactor);
        }
        user.UpdatedAt = DateTime.UtcNow;

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            @"UPDATE config_users SET role = @Role, password_hash = @PasswordHash, updated_at = @UpdatedAt
              WHERE LOWER(username) = LOWER(@Username)",
            user);

        return user;
    }

    public async Task<bool> DeleteUserAsync(string username)
    {
        var normalized = NormalizeUsername(username);
        await using var connection = await OpenAsync();
        var affected = await connection.ExecuteAsync(
            "DELETE FROM config_users WHERE LOWER(username) = LOWER(@normalized)", new { normalized });
        return affected > 0;
    }

    public async Task ChangePasswordAsync(string username, string newPassword)
    {
        var normalized = NormalizeUsername(username);
        var hash = BCrypt.Net.BCrypt.HashPassword(newPassword, BcryptWorkFactor);
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            @"UPDATE config_users SET password_hash = @hash, updated_at = @updatedAt
              WHERE LOWER(username) = LOWER(@normalized)",
            new { hash, updatedAt = DateTime.UtcNow, normalized });
    }
}
