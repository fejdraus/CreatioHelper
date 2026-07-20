using System.Text.Json;
using System.Text.Json.Nodes;
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

            if (users2.Count == 0)
            {
                _logger.LogWarning(
                    "No administrator account is configured, so every sign-in will be rejected. " +
                    "Add a user to the \"Authentication:Users\" section with the plain password in the " +
                    "\"Password\" field and restart the agent: the password is hashed on startup and " +
                    "removed from the configuration file. Alternatively, create {FilePath} manually.",
                    _filePath);
                return;
            }

            await SaveUsersInternalAsync(users2);
            _logger.LogInformation("Created users.json with {Count} users", users2.Count);

            ClearMigratedPasswordsFromConfigFiles();
        }
        finally
        {
            _lock.Release();
        }
    }

        private void ClearMigratedPasswordsFromConfigFiles()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var candidates = new List<string> { "appsettings.json" };
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            candidates.Add($"appsettings.{environmentName}.json");
        }
        var cleared = false;
        foreach (var fileName in candidates)
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (TryClearPasswordsInFile(path))
            {
                cleared = true;
                _logger.LogInformation("Removed migrated plain passwords from {File}", path);
            }
        }
        if (!cleared)
        {
            _logger.LogWarning(
                "Migrated credentials were not found in any configuration file. If they came from " +
                "environment variables or user secrets, remove them there manually - they are now " +
                "stored hashed in {FilePath}.", _filePath);
        }
    }
    private bool TryClearPasswordsInFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root?["Authentication"] is not JsonObject auth || auth["Users"] is not JsonArray users)
            {
                return false;
            }
            var hadPasswords = users.OfType<JsonObject>()
                .Any(user => !string.IsNullOrEmpty(user["Password"]?.GetValue<string>()));
            if (!hadPasswords)
            {
                return false;
            }
            auth["Users"] = new JsonArray();
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove migrated passwords from {File}", path);
            return false;
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
        private async Task<List<StoredUser>> LoadUsersInternalAsync()
    {
        if (_cache != null) return _cache;
        if (!File.Exists(_filePath))
            return _cache = new List<StoredUser>();
        var json = await File.ReadAllTextAsync(_filePath);
        _cache = JsonSerializer.Deserialize<List<StoredUser>>(json, JsonOptions) ?? new List<StoredUser>();
        return _cache;
    }

        private async Task SaveUsersInternalAsync(List<StoredUser> users)
    {
        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(users, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
        _cache = users;
    }
}