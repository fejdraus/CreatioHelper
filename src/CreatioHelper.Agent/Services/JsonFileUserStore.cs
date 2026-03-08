using System.Text.Json;
using CreatioHelper.Agent.Configuration;
using CreatioHelper.Agent.Models;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Agent.Services;

public class JsonFileUserStore : IUserStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] ValidRoles = ["admin", "user", "readonly", "monitor"];
    private const int BcryptWorkFactor = 12;

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<JsonFileUserStore> _logger;
    private readonly AuthenticationSettings _authSettings;
    private List<StoredUser>? _cache;

    public JsonFileUserStore(
        ILogger<JsonFileUserStore> logger,
        IOptions<AuthenticationSettings> authSettings,
        IConfiguration configuration)
    {
        _logger = logger;
        _authSettings = authSettings.Value;

        var configDir = configuration["UserStore:Path"]
            ?? Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(configDir);
        _filePath = Path.Combine(configDir, "users.json");

        // Run migration synchronously during construction to ensure store is ready
        MigrateIfNeededAsync().GetAwaiter().GetResult();
    }

    public async Task<IReadOnlyList<StoredUser>> GetAllUsersAsync()
    {
        var users = await LoadUsersAsync();
        return users.AsReadOnly();
    }

    public async Task<StoredUser?> GetUserAsync(string username)
    {
        var users = await LoadUsersAsync();
        return users.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> ValidatePasswordAsync(string username, string password)
    {
        var user = await GetUserAsync(username);
        if (user == null) return false;

        // Auto-rehash: if stored hash is plaintext (not bcrypt), upgrade it
        if (!user.PasswordHash.StartsWith("$2"))
        {
            if (user.PasswordHash == password)
            {
                _logger.LogInformation("Auto-rehashing plaintext password for user: {Username}", username);
                await ChangePasswordAsync(username, password);
                return true;
            }
            return false;
        }

        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
    }

    public async Task<StoredUser> CreateUserAsync(string username, string password, string role)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required.", nameof(password));
        if (!ValidRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid role. Valid roles: {string.Join(", ", ValidRoles)}", nameof(role));

        await _lock.WaitAsync();
        try
        {
            var users = await LoadUsersInternalAsync();

            if (users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"User '{username}' already exists.");

            var user = new StoredUser
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor),
                Role = role.ToLowerInvariant(),
                CreatedAt = DateTime.UtcNow
            };

            users.Add(user);
            await SaveUsersInternalAsync(users);

            _logger.LogInformation("Created user: {Username} with role: {Role}", username, role);
            return user;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredUser> UpdateUserAsync(string username, string? newRole, string? newPassword)
    {
        if (newRole != null && !ValidRoles.Contains(newRole, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid role. Valid roles: {string.Join(", ", ValidRoles)}", nameof(newRole));

        await _lock.WaitAsync();
        try
        {
            var users = await LoadUsersInternalAsync();
            var user = users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
                throw new KeyNotFoundException($"User '{username}' not found.");

            if (newRole != null)
                user.Role = newRole.ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(newPassword))
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, BcryptWorkFactor);

            user.UpdatedAt = DateTime.UtcNow;
            await SaveUsersInternalAsync(users);

            _logger.LogInformation("Updated user: {Username}", username);
            return user;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteUserAsync(string username)
    {
        await _lock.WaitAsync();
        try
        {
            var users = await LoadUsersInternalAsync();
            var user = users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

            if (user == null) return false;

            // Prevent deleting the last admin
            if (string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase))
            {
                var adminCount = users.Count(u =>
                    string.Equals(u.Role, "admin", StringComparison.OrdinalIgnoreCase));
                if (adminCount <= 1)
                    throw new InvalidOperationException("Cannot delete the last admin user.");
            }

            users.Remove(user);
            await SaveUsersInternalAsync(users);

            _logger.LogInformation("Deleted user: {Username}", username);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ChangePasswordAsync(string username, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            throw new ArgumentException("Password is required.", nameof(newPassword));

        await _lock.WaitAsync();
        try
        {
            var users = await LoadUsersInternalAsync();
            var user = users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
                throw new KeyNotFoundException($"User '{username}' not found.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, BcryptWorkFactor);
            user.UpdatedAt = DateTime.UtcNow;
            await SaveUsersInternalAsync(users);

            _logger.LogInformation("Password changed for user: {Username}", username);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task MigrateIfNeededAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(_filePath))
            {
                // Check for plaintext passwords that need rehashing
                var users = await LoadUsersInternalAsync();
                var needsSave = false;

                foreach (var user in users)
                {
                    if (!user.PasswordHash.StartsWith("$2"))
                    {
                        _logger.LogInformation("Rehashing legacy plaintext password for user: {Username}", user.Username);
                        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(user.PasswordHash, BcryptWorkFactor);
                        user.UpdatedAt = DateTime.UtcNow;
                        needsSave = true;
                    }
                }

                if (needsSave)
                    await SaveUsersInternalAsync(users);

                return;
            }

            // Migrate from appsettings
            var users2 = new List<StoredUser>();

            if (_authSettings.Users.Count > 0)
            {
                _logger.LogInformation("Migrating {Count} users from appsettings to users.json", _authSettings.Users.Count);

                foreach (var configUser in _authSettings.Users)
                {
                    users2.Add(new StoredUser
                    {
                        Username = configUser.Username,
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword(configUser.Password, BcryptWorkFactor),
                        Role = configUser.Role?.ToLowerInvariant() ?? "user",
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            // Ensure at least one admin exists
            if (!users2.Any(u => string.Equals(u.Role, "admin", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("No admin users found. Creating default admin user (admin/admin123). Change this password immediately!");
                users2.Add(new StoredUser
                {
                    Username = "admin",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123", BcryptWorkFactor),
                    Role = "admin",
                    CreatedAt = DateTime.UtcNow
                });
            }

            await SaveUsersInternalAsync(users2);
            _logger.LogInformation("Created users.json with {Count} users", users2.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<StoredUser>> LoadUsersAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await LoadUsersInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Load users without acquiring the lock. Caller must hold _lock.
    /// </summary>
    private async Task<List<StoredUser>> LoadUsersInternalAsync()
    {
        if (_cache != null) return _cache;

        if (!File.Exists(_filePath))
            return _cache = new List<StoredUser>();

        var json = await File.ReadAllTextAsync(_filePath);
        _cache = JsonSerializer.Deserialize<List<StoredUser>>(json, JsonOptions) ?? new List<StoredUser>();
        return _cache;
    }

    /// <summary>
    /// Save users atomically (temp + rename). Caller must hold _lock.
    /// </summary>
    private async Task SaveUsersInternalAsync(List<StoredUser> users)
    {
        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(users, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
        _cache = users;
    }
}
