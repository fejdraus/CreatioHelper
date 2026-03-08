using CreatioHelper.Agent.Configuration;
using CreatioHelper.Agent.Controllers;
using CreatioHelper.Agent.Models;
using CreatioHelper.Agent.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Claims;

namespace CreatioHelper.Agent.Tests;

public class UserManagementTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileUserStore _userStore;

    public UserManagementTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"usermgmt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var authSettings = Options.Create(new AuthenticationSettings
        {
            Users = new List<UserCredentials>
            {
                new() { Username = "admin", Password = "admin123", Role = "admin" },
                new() { Username = "user1", Password = "pass1", Role = "user" }
            }
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserStore:Path"] = _tempDir
            })
            .Build();

        var logger = new Mock<ILogger<JsonFileUserStore>>();

        _userStore = new JsonFileUserStore(logger.Object, authSettings, config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    #region Migration Tests

    [Fact]
    public async Task Migration_CreatesUsersJsonFromAppsettings()
    {
        var usersFile = Path.Combine(_tempDir, "users.json");
        Assert.True(File.Exists(usersFile));

        var users = await _userStore.GetAllUsersAsync();
        Assert.Equal(2, users.Count);
        Assert.Contains(users, u => u.Username == "admin" && u.Role == "admin");
        Assert.Contains(users, u => u.Username == "user1" && u.Role == "user");
    }

    [Fact]
    public async Task Migration_HashesPasswords_WithBcrypt()
    {
        var users = await _userStore.GetAllUsersAsync();
        foreach (var user in users)
        {
            Assert.StartsWith("$2", user.PasswordHash);
        }
    }

    #endregion

    #region Bcrypt Verify Tests

    [Fact]
    public async Task ValidatePassword_ReturnsTrue_ForCorrectPassword()
    {
        var result = await _userStore.ValidatePasswordAsync("admin", "admin123");
        Assert.True(result);
    }

    [Fact]
    public async Task ValidatePassword_ReturnsFalse_ForWrongPassword()
    {
        var result = await _userStore.ValidatePasswordAsync("admin", "wrongpassword");
        Assert.False(result);
    }

    [Fact]
    public async Task ValidatePassword_ReturnsFalse_ForNonexistentUser()
    {
        var result = await _userStore.ValidatePasswordAsync("nonexistent", "any");
        Assert.False(result);
    }

    #endregion

    #region CRUD Tests

    [Fact]
    public async Task CreateUser_AddsNewUser()
    {
        var user = await _userStore.CreateUserAsync("newuser", "newpass", "user");

        Assert.Equal("newuser", user.Username);
        Assert.Equal("user", user.Role);
        Assert.StartsWith("$2", user.PasswordHash);

        var all = await _userStore.GetAllUsersAsync();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task CreateUser_ThrowsOnDuplicate()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userStore.CreateUserAsync("admin", "pass", "user"));
    }

    [Fact]
    public async Task CreateUser_ThrowsOnInvalidRole()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _userStore.CreateUserAsync("x", "pass", "superadmin"));
    }

    [Fact]
    public async Task UpdateUser_ChangesRole()
    {
        var updated = await _userStore.UpdateUserAsync("user1", "admin", null);
        Assert.Equal("admin", updated.Role);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task UpdateUser_ChangesPassword()
    {
        await _userStore.UpdateUserAsync("user1", null, "newpassword");
        var result = await _userStore.ValidatePasswordAsync("user1", "newpassword");
        Assert.True(result);
    }

    [Fact]
    public async Task UpdateUser_ThrowsForNonexistentUser()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _userStore.UpdateUserAsync("ghost", "admin", null));
    }

    [Fact]
    public async Task DeleteUser_RemovesUser()
    {
        var deleted = await _userStore.DeleteUserAsync("user1");
        Assert.True(deleted);

        var all = await _userStore.GetAllUsersAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task DeleteUser_ReturnsFalse_ForNonexistentUser()
    {
        var deleted = await _userStore.DeleteUserAsync("ghost");
        Assert.False(deleted);
    }

    #endregion

    #region Last Admin Protection

    [Fact]
    public async Task DeleteUser_ThrowsWhenDeletingLastAdmin()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userStore.DeleteUserAsync("admin"));
        Assert.Contains("last admin", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteUser_AllowsDeletingAdmin_WhenMultipleAdminsExist()
    {
        await _userStore.CreateUserAsync("admin2", "pass", "admin");
        var deleted = await _userStore.DeleteUserAsync("admin");
        Assert.True(deleted);
    }

    #endregion

    #region Change Password

    [Fact]
    public async Task ChangePassword_UpdatesPasswordHash()
    {
        await _userStore.ChangePasswordAsync("admin", "newadminpass");
        var result = await _userStore.ValidatePasswordAsync("admin", "newadminpass");
        Assert.True(result);

        // Old password should not work
        var oldResult = await _userStore.ValidatePasswordAsync("admin", "admin123");
        Assert.False(oldResult);
    }

    [Fact]
    public async Task ChangePassword_ThrowsForNonexistentUser()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _userStore.ChangePasswordAsync("ghost", "pass"));
    }

    #endregion

    #region Auto-Rehash Plaintext

    [Fact]
    public async Task AutoRehash_ConvertsPlaintextOnValidation()
    {
        // Create a fresh store with no existing users.json, forcing migration
        var tempDir2 = Path.Combine(Path.GetTempPath(), $"usermgmt_rehash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir2);

        try
        {
            // Write a users.json with plaintext password
            var usersJson = """
            [
                {
                    "username": "test",
                    "passwordHash": "plaintext123",
                    "role": "admin",
                    "createdAt": "2025-01-01T00:00:00Z"
                }
            ]
            """;
            File.WriteAllText(Path.Combine(tempDir2, "users.json"), usersJson);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UserStore:Path"] = tempDir2
                })
                .Build();

            var logger = new Mock<ILogger<JsonFileUserStore>>();
            var authSettings = Options.Create(new AuthenticationSettings());

            // Constructor triggers migration which rehashes plaintext
            var store = new JsonFileUserStore(logger.Object, authSettings, config);

            // After migration, the hash should be bcrypt
            var user = await store.GetUserAsync("test");
            Assert.NotNull(user);
            Assert.StartsWith("$2", user!.PasswordHash);

            // Password should still validate
            var valid = await store.ValidatePasswordAsync("test", "plaintext123");
            Assert.True(valid);
        }
        finally
        {
            try { Directory.Delete(tempDir2, true); } catch { }
        }
    }

    #endregion

    #region Default Admin Creation

    [Fact]
    public async Task DefaultAdmin_CreatedWhenNoUsers()
    {
        var tempDir3 = Path.Combine(Path.GetTempPath(), $"usermgmt_default_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir3);

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UserStore:Path"] = tempDir3
                })
                .Build();

            var logger = new Mock<ILogger<JsonFileUserStore>>();
            var authSettings = Options.Create(new AuthenticationSettings { Users = new() });

            var store = new JsonFileUserStore(logger.Object, authSettings, config);

            var users = await store.GetAllUsersAsync();
            Assert.Single(users);
            Assert.Equal("admin", users[0].Username);
            Assert.Equal("admin", users[0].Role);

            var valid = await store.ValidatePasswordAsync("admin", "admin123");
            Assert.True(valid);
        }
        finally
        {
            try { Directory.Delete(tempDir3, true); } catch { }
        }
    }

    #endregion
}
