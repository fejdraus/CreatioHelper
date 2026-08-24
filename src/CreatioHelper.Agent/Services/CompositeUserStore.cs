using CreatioHelper.Agent.Models;

namespace CreatioHelper.Agent.Services;

/// <summary>
/// Keeps the bootstrap administrator in the file store (break-glass, always available even
/// if the database is empty or unreachable) and all other users in the database store.
/// </summary>
public class CompositeUserStore : IUserStore
{
    private const string AdminRole = "admin";

    private readonly JsonFileUserStore _fileStore;
    private readonly DbUserStore _dbStore;
    private readonly ILogger<CompositeUserStore> _logger;
    private readonly SemaphoreSlim _migrateLock = new(1, 1);
    private bool _migrated;

    public CompositeUserStore(
        JsonFileUserStore fileStore,
        DbUserStore dbStore,
        ILogger<CompositeUserStore> logger)
    {
        _fileStore = fileStore;
        _dbStore = dbStore;
        _logger = logger;
    }

    /// <summary>
    /// One-time move of non-admin users out of the file store into the database, leaving only
    /// the bootstrap administrator(s) in the file.
    /// </summary>
    private async Task EnsureMigratedAsync()
    {
        if (_migrated)
        {
            return;
        }

        await _migrateLock.WaitAsync();
        try
        {
            if (_migrated)
            {
                return;
            }

            var fileUsers = await _fileStore.GetAllUsersAsync();
            var nonAdmins = fileUsers
                .Where(u => !string.Equals(u.Role, AdminRole, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var user in nonAdmins)
            {
                await _dbStore.ImportUserAsync(user);
                await _fileStore.DeleteUserAsync(user.Username);
                _logger.LogInformation("Moved user {Username} from the file store to the database", user.Username);
            }

            _migrated = true;
        }
        finally
        {
            _migrateLock.Release();
        }
    }

    private async Task<bool> IsBootstrapAsync(string username) =>
        await _fileStore.GetUserAsync(username) != null;

    public async Task<IReadOnlyList<StoredUser>> GetAllUsersAsync()
    {
        await EnsureMigratedAsync();
        var fileUsers = await _fileStore.GetAllUsersAsync();
        var dbUsers = await _dbStore.GetAllUsersAsync();

        var bootstrapNames = fileUsers
            .Select(u => u.Username)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = new List<StoredUser>(fileUsers);
        merged.AddRange(dbUsers.Where(u => !bootstrapNames.Contains(u.Username)));
        return merged;
    }

    public async Task<StoredUser?> GetUserAsync(string username)
    {
        await EnsureMigratedAsync();
        return await _fileStore.GetUserAsync(username) ?? await _dbStore.GetUserAsync(username);
    }

    public async Task<bool> ValidatePasswordAsync(string username, string password)
    {
        await EnsureMigratedAsync();
        return await IsBootstrapAsync(username)
            ? await _fileStore.ValidatePasswordAsync(username, password)
            : await _dbStore.ValidatePasswordAsync(username, password);
    }

    public async Task<StoredUser> CreateUserAsync(string username, string password, string role)
    {
        await EnsureMigratedAsync();
        if (await IsBootstrapAsync(username))
        {
            throw new InvalidOperationException($"User '{username}' already exists.");
        }
        return await _dbStore.CreateUserAsync(username, password, role);
    }

    public async Task<StoredUser> UpdateUserAsync(string username, string? newRole, string? newPassword)
    {
        await EnsureMigratedAsync();
        return await IsBootstrapAsync(username)
            ? await _fileStore.UpdateUserAsync(username, newRole, newPassword)
            : await _dbStore.UpdateUserAsync(username, newRole, newPassword);
    }

    public async Task<bool> DeleteUserAsync(string username)
    {
        await EnsureMigratedAsync();
        if (await IsBootstrapAsync(username))
        {
            throw new InvalidOperationException(
                "The bootstrap administrator is kept in the configuration file and cannot be deleted here.");
        }
        return await _dbStore.DeleteUserAsync(username);
    }

    public async Task ChangePasswordAsync(string username, string newPassword)
    {
        await EnsureMigratedAsync();
        if (await IsBootstrapAsync(username))
        {
            await _fileStore.ChangePasswordAsync(username, newPassword);
        }
        else
        {
            await _dbStore.ChangePasswordAsync(username, newPassword);
        }
    }
}
