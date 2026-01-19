using CreatioHelper.Infrastructure.Services.Security;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Security;

/// <summary>
/// Tests for CsrfProtectionService.
/// </summary>
public class CsrfProtectionServiceTests : IDisposable
{
    private readonly Mock<ILogger<CsrfProtectionService>> _loggerMock;
    private CsrfProtectionService _service;

    public CsrfProtectionServiceTests()
    {
        _loggerMock = new Mock<ILogger<CsrfProtectionService>>();
        _service = new CsrfProtectionService(_loggerMock.Object);
    }

    public void Dispose()
    {
        _service?.Dispose();
    }

    #region Token Generation Tests

    [Fact]
    public void GenerateToken_ReturnsNonEmptyToken()
    {
        var token = _service.GenerateToken();

        Assert.NotEmpty(token);
        Assert.Equal(64, token.Length); // 32 bytes = 64 hex chars
    }

    [Fact]
    public void GenerateToken_ReturnsUniqueTokens()
    {
        var tokens = new HashSet<string>();

        for (int i = 0; i < 100; i++)
        {
            var token = _service.GenerateToken();
            Assert.True(tokens.Add(token), "Token should be unique");
        }
    }

    [Fact]
    public void GenerateToken_WithSession_TracksSessionTokens()
    {
        var sessionId = "test-session";

        var token = _service.GenerateToken(sessionId);

        Assert.NotEmpty(token);
        var stats = _service.GetStatistics();
        Assert.Equal(1, stats.SessionCount);
    }

    [Fact]
    public void GenerateToken_WhenDisabled_ReturnsEmpty()
    {
        var config = new CsrfConfiguration { Enabled = false };
        using var service = new CsrfProtectionService(_loggerMock.Object, config);

        var token = service.GenerateToken();

        Assert.Empty(token);
    }

    [Fact]
    public void GenerateToken_EnforcesMaxTokensPerSession()
    {
        var config = new CsrfConfiguration { MaxTokensPerSession = 3 };
        using var service = new CsrfProtectionService(_loggerMock.Object, config);
        var sessionId = "test-session";

        // Generate more tokens than max
        for (int i = 0; i < 5; i++)
        {
            service.GenerateToken(sessionId);
        }

        var stats = service.GetStatistics();
        Assert.True(stats.ActiveTokenCount <= 3);
    }

    #endregion

    #region Token Validation Tests

    [Fact]
    public void ValidateToken_ValidToken_ReturnsTrue()
    {
        var token = _service.GenerateToken();

        var result = _service.ValidateToken(token);

        Assert.True(result);
    }

    [Fact]
    public void ValidateToken_InvalidToken_ReturnsFalse()
    {
        var result = _service.ValidateToken("invalid-token");

        Assert.False(result);
    }

    [Fact]
    public void ValidateToken_EmptyToken_ReturnsFalse()
    {
        var result = _service.ValidateToken(string.Empty);

        Assert.False(result);
    }

    [Fact]
    public void ValidateToken_NullToken_ReturnsFalse()
    {
        var result = _service.ValidateToken(null!);

        Assert.False(result);
    }

    [Fact]
    public void ValidateToken_WhenDisabled_ReturnsTrue()
    {
        var config = new CsrfConfiguration { Enabled = false };
        using var service = new CsrfProtectionService(_loggerMock.Object, config);

        var result = service.ValidateToken("any-token");

        Assert.True(result);
    }

    [Fact]
    public void ValidateToken_ExpiredToken_ReturnsFalse()
    {
        var config = new CsrfConfiguration { TokenLifetimeMinutes = 0 };
        using var service = new CsrfProtectionService(_loggerMock.Object, config);
        var token = service.GenerateToken();

        // Token expires immediately
        Thread.Sleep(50);
        var result = service.ValidateToken(token);

        Assert.False(result);
    }

    [Fact]
    public void ValidateToken_WithMatchingSession_ReturnsTrue()
    {
        var sessionId = "test-session";
        var token = _service.GenerateToken(sessionId);

        var result = _service.ValidateToken(token, sessionId);

        Assert.True(result);
    }

    [Fact]
    public void ValidateToken_WithMismatchedSession_ReturnsFalse()
    {
        var token = _service.GenerateToken("session1");

        var result = _service.ValidateToken(token, "session2");

        Assert.False(result);
    }

    [Fact]
    public void ValidateToken_UpdatesLastUsedTime()
    {
        var token = _service.GenerateToken();

        _service.ValidateToken(token);
        var stats = _service.GetStatistics();

        Assert.Equal(1, stats.SuccessfulValidations);
    }

    #endregion

    #region RequiresValidation Tests

    [Fact]
    public void RequiresValidation_PostMethod_ReturnsTrue()
    {
        var result = _service.RequiresValidation("POST");

        Assert.True(result);
    }

    [Fact]
    public void RequiresValidation_GetMethod_ReturnsFalse()
    {
        var result = _service.RequiresValidation("GET");

        Assert.False(result);
    }

    [Fact]
    public void RequiresValidation_PutMethod_ReturnsTrue()
    {
        var result = _service.RequiresValidation("PUT");

        Assert.True(result);
    }

    [Fact]
    public void RequiresValidation_DeleteMethod_ReturnsTrue()
    {
        var result = _service.RequiresValidation("DELETE");

        Assert.True(result);
    }

    [Fact]
    public void RequiresValidation_WhenDisabled_ReturnsFalse()
    {
        var config = new CsrfConfiguration { Enabled = false };
        using var service = new CsrfProtectionService(_loggerMock.Object, config);

        var result = service.RequiresValidation("POST");

        Assert.False(result);
    }

    #endregion

    #region Path Exemption Tests

    [Fact]
    public void IsPathExempt_ExemptPath_ReturnsTrue()
    {
        var config = new CsrfConfiguration
        {
            ExemptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/api/health", "/api/metrics" }
        };
        using var service = new CsrfProtectionService(_loggerMock.Object, config);

        Assert.True(service.IsPathExempt("/api/health"));
        Assert.True(service.IsPathExempt("/api/health/check"));
        Assert.True(service.IsPathExempt("/api/metrics"));
    }

    [Fact]
    public void IsPathExempt_NonExemptPath_ReturnsFalse()
    {
        var config = new CsrfConfiguration
        {
            ExemptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/api/health" }
        };
        using var service = new CsrfProtectionService(_loggerMock.Object, config);

        Assert.False(service.IsPathExempt("/api/users"));
        Assert.False(service.IsPathExempt("/api/data"));
    }

    [Fact]
    public void IsPathExempt_EmptyPath_ReturnsFalse()
    {
        Assert.False(_service.IsPathExempt(string.Empty));
        Assert.False(_service.IsPathExempt(null!));
    }

    #endregion

    #region Token Revocation Tests

    [Fact]
    public void RevokeToken_RemovesToken()
    {
        var token = _service.GenerateToken();
        Assert.True(_service.ValidateToken(token));

        _service.RevokeToken(token);

        Assert.False(_service.ValidateToken(token));
    }

    [Fact]
    public void RevokeToken_NonExistentToken_NoError()
    {
        // Should not throw
        _service.RevokeToken("non-existent-token");
    }

    [Fact]
    public void RevokeSessionTokens_RemovesAllSessionTokens()
    {
        var sessionId = "test-session";
        var token1 = _service.GenerateToken(sessionId);
        var token2 = _service.GenerateToken(sessionId);
        var token3 = _service.GenerateToken(sessionId);

        _service.RevokeSessionTokens(sessionId);

        Assert.False(_service.ValidateToken(token1));
        Assert.False(_service.ValidateToken(token2));
        Assert.False(_service.ValidateToken(token3));
    }

    [Fact]
    public void RevokeSessionTokens_DoesNotAffectOtherSessions()
    {
        var token1 = _service.GenerateToken("session1");
        var token2 = _service.GenerateToken("session2");

        _service.RevokeSessionTokens("session1");

        Assert.False(_service.ValidateToken(token1));
        Assert.True(_service.ValidateToken(token2));
    }

    #endregion

    #region Cleanup Tests

    [Fact]
    public void CleanupExpiredTokens_RemovesExpiredTokens()
    {
        var config = new CsrfConfiguration { TokenLifetimeMinutes = 0 };
        using var service = new CsrfProtectionService(_loggerMock.Object, config);

        service.GenerateToken();
        service.GenerateToken();
        Thread.Sleep(50);

        var removed = service.CleanupExpiredTokens();

        Assert.Equal(2, removed);
        Assert.Equal(0, service.GetStatistics().ActiveTokenCount);
    }

    [Fact]
    public void CleanupExpiredTokens_KeepsValidTokens()
    {
        var token = _service.GenerateToken();

        var removed = _service.CleanupExpiredTokens();

        Assert.Equal(0, removed);
        Assert.True(_service.ValidateToken(token));
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        _service.GenerateToken("session1");
        _service.GenerateToken("session1");
        _service.GenerateToken("session2");
        _service.ValidateToken("invalid");

        var stats = _service.GetStatistics();

        Assert.Equal(3, stats.ActiveTokenCount);
        Assert.Equal(2, stats.SessionCount);
        Assert.Equal(3, stats.TotalTokensGenerated);
        Assert.Equal(1, stats.TotalValidationAttempts);
        Assert.Equal(1, stats.FailedValidations);
    }

    [Fact]
    public void GetConfiguration_ReturnsConfiguration()
    {
        var config = _service.GetConfiguration();

        Assert.True(config.Enabled);
        Assert.Equal(32, config.TokenLengthBytes);
    }

    #endregion
}
