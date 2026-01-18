using CreatioHelper.Infrastructure.Services.Sync.Security;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.Security;

public class ApiTokenAuthServiceTests : IDisposable
{
    private readonly Mock<ILogger<ApiTokenAuthService>> _loggerMock;
    private readonly ApiTokenAuthService _authService;
    private readonly string _tempStoragePath;

    public ApiTokenAuthServiceTests()
    {
        _loggerMock = new Mock<ILogger<ApiTokenAuthService>>();
        _tempStoragePath = Path.Combine(Path.GetTempPath(), $"test_tokens_{Guid.NewGuid():N}.json");
        _authService = new ApiTokenAuthService(_loggerMock.Object, _tempStoragePath);
    }

    public void Dispose()
    {
        if (File.Exists(_tempStoragePath))
        {
            File.Delete(_tempStoragePath);
        }
    }

    [Fact]
    public void GenerateToken_CreatesValidToken()
    {
        // Act
        var (token, rawToken) = _authService.GenerateToken("Test Token");

        // Assert
        Assert.NotNull(token);
        Assert.NotEmpty(token.Id);
        Assert.Equal("Test Token", token.Name);
        Assert.NotEmpty(token.TokenHash);
        Assert.True(token.Enabled);
        Assert.StartsWith("st-", rawToken);
    }

    [Fact]
    public void GenerateToken_WithScopes_SetsScopes()
    {
        // Arrange
        var scopes = new[] { ApiScopes.ReadConfig, ApiScopes.WriteConfig };

        // Act
        var (token, _) = _authService.GenerateToken("Test Token", scopes);

        // Assert
        Assert.Contains(ApiScopes.ReadConfig, token.Scopes);
        Assert.Contains(ApiScopes.WriteConfig, token.Scopes);
    }

    [Fact]
    public void GenerateToken_WithExpiry_SetsExpiryTime()
    {
        // Act
        var (token, _) = _authService.GenerateToken("Test Token", expiry: TimeSpan.FromHours(1));

        // Assert
        Assert.NotNull(token.ExpiresAt);
        Assert.True(token.ExpiresAt > DateTime.UtcNow);
        Assert.True(token.ExpiresAt < DateTime.UtcNow.AddHours(2));
    }

    [Fact]
    public async Task ValidateTokenAsync_ValidToken_ReturnsValid()
    {
        // Arrange
        var (_, rawToken) = _authService.GenerateToken("Test Token");

        // Act
        var result = await _authService.ValidateTokenAsync(rawToken);

        // Assert
        Assert.True(result.IsValid);
        Assert.NotNull(result.Token);
        Assert.Equal("Test Token", result.Token.Name);
    }

    [Fact]
    public async Task ValidateTokenAsync_InvalidToken_ReturnsInvalid()
    {
        // Act
        var result = await _authService.ValidateTokenAsync("st-invalid-token");

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Token not found", result.Error);
    }

    [Fact]
    public async Task ValidateTokenAsync_EmptyToken_ReturnsInvalid()
    {
        // Act
        var result = await _authService.ValidateTokenAsync("");

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Token is required", result.Error);
    }

    [Fact]
    public async Task ValidateTokenAsync_WrongFormat_ReturnsInvalid()
    {
        // Act
        var result = await _authService.ValidateTokenAsync("wrong-format-token");

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Invalid token format", result.Error);
    }

    [Fact]
    public async Task ValidateTokenAsync_ExpiredToken_ReturnsInvalid()
    {
        // Arrange
        var (token, rawToken) = _authService.GenerateToken("Test Token", expiry: TimeSpan.FromMilliseconds(1));
        await Task.Delay(10); // Wait for expiry

        // Act
        var result = await _authService.ValidateTokenAsync(rawToken);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Token has expired", result.Error);
    }

    [Fact]
    public async Task ValidateTokenAsync_DisabledToken_ReturnsInvalid()
    {
        // Arrange
        var (token, rawToken) = _authService.GenerateToken("Test Token");
        var disabledToken = token with { Enabled = false };
        await _authService.UpdateTokenAsync(disabledToken);

        // Act
        var result = await _authService.ValidateTokenAsync(rawToken);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Token is disabled", result.Error);
    }

    [Fact]
    public async Task ValidateTokenAsync_WithRequiredScope_ChecksScope()
    {
        // Arrange
        var (_, rawToken) = _authService.GenerateToken("Test Token", new[] { ApiScopes.ReadConfig });

        // Act
        var validResult = await _authService.ValidateTokenAsync(rawToken, ApiScopes.ReadConfig);
        var invalidResult = await _authService.ValidateTokenAsync(rawToken, ApiScopes.WriteConfig);

        // Assert
        Assert.True(validResult.IsValid);
        Assert.False(invalidResult.IsValid);
        Assert.Contains("lacks required scope", invalidResult.Error);
    }

    [Fact]
    public async Task ValidateTokenAsync_AdminScope_GrantsAllAccess()
    {
        // Arrange
        var (_, rawToken) = _authService.GenerateToken("Admin Token", new[] { ApiScopes.Admin });

        // Act
        var result = await _authService.ValidateTokenAsync(rawToken, ApiScopes.WriteConfig);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task RevokeTokenAsync_RemovesToken()
    {
        // Arrange
        var (token, rawToken) = _authService.GenerateToken("Test Token");

        // Act
        var revoked = await _authService.RevokeTokenAsync(token.Id);
        var validationResult = await _authService.ValidateTokenAsync(rawToken);

        // Assert
        Assert.True(revoked);
        Assert.False(validationResult.IsValid);
    }

    [Fact]
    public async Task RevokeTokenAsync_NonExistentToken_ReturnsFalse()
    {
        // Act
        var result = await _authService.RevokeTokenAsync("non-existent-id");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ListTokensAsync_ReturnsAllTokens()
    {
        // Arrange
        _authService.GenerateToken("Token 1");
        _authService.GenerateToken("Token 2");

        // Act
        var tokens = await _authService.ListTokensAsync();

        // Assert
        Assert.Equal(2, tokens.Count);
    }

    [Fact]
    public async Task GetTokenAsync_ReturnsCorrectToken()
    {
        // Arrange
        var (token, _) = _authService.GenerateToken("Test Token");

        // Act
        var retrieved = await _authService.GetTokenAsync(token.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(token.Id, retrieved.Id);
        Assert.Equal(token.Name, retrieved.Name);
    }

    [Fact]
    public async Task GetTokenAsync_NonExistent_ReturnsNull()
    {
        // Act
        var result = await _authService.GetTokenAsync("non-existent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ScopeGrantsAccess_DirectMatch_ReturnsTrue()
    {
        // Arrange
        var scopes = new[] { ApiScopes.ReadConfig };

        // Act
        var result = _authService.ScopeGrantsAccess(scopes, ApiScopes.ReadConfig);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ScopeGrantsAccess_NoMatch_ReturnsFalse()
    {
        // Arrange
        var scopes = new[] { ApiScopes.ReadConfig };

        // Act
        var result = _authService.ScopeGrantsAccess(scopes, ApiScopes.WriteConfig);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ScopeGrantsAccess_AdminGrantsAll_ReturnsTrue()
    {
        // Arrange
        var scopes = new[] { ApiScopes.Admin };

        // Act
        var result = _authService.ScopeGrantsAccess(scopes, ApiScopes.WriteConfig);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ApiTokenAuthMiddleware_ExtractToken_BearerFormat()
    {
        // Act
        var token = ApiTokenAuthMiddleware.ExtractToken("Bearer st-test-token");

        // Assert
        Assert.Equal("st-test-token", token);
    }

    [Fact]
    public void ApiTokenAuthMiddleware_ExtractToken_XApiKeyFormat()
    {
        // Act
        var token = ApiTokenAuthMiddleware.ExtractToken("X-API-Key st-test-token");

        // Assert
        Assert.Equal("st-test-token", token);
    }

    [Fact]
    public void ApiTokenAuthMiddleware_ExtractToken_RawToken()
    {
        // Act
        var token = ApiTokenAuthMiddleware.ExtractToken("st-test-token");

        // Assert
        Assert.Equal("st-test-token", token);
    }

    [Fact]
    public void ApiTokenAuthMiddleware_ExtractToken_NullInput_ReturnsNull()
    {
        // Act
        var token = ApiTokenAuthMiddleware.ExtractToken(null);

        // Assert
        Assert.Null(token);
    }
}
