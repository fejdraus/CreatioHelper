using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CreatioHelper.Tests.Security;

public class LdapAuthServiceTests
{
    [Theory]
    [InlineData(",", "\\,")]
    [InlineData("+", "\\+")]
    [InlineData("\"", "\\\"")]
    [InlineData("\\", "\\\\")]
    [InlineData("<", "\\<")]
    [InlineData(">", "\\>")]
    [InlineData(";", "\\;")]
    [InlineData("cn=admin,dc=example", "cn=admin\\,dc=example")]
    [InlineData("user+name", "user\\+name")]
    public void EscapeForLdapDN_EscapesSpecialCharacters(string input, string expected)
    {
        var result = LdapAuthService.EscapeForLdapDN(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EscapeForLdapDN_EscapesLeadingHash()
    {
        Assert.Equal("\\#admin", LdapAuthService.EscapeForLdapDN("#admin"));
    }

    [Fact]
    public void EscapeForLdapDN_DoesNotEscapeMiddleHash()
    {
        Assert.Equal("ad#min", LdapAuthService.EscapeForLdapDN("ad#min"));
    }

    [Fact]
    public void EscapeForLdapDN_EscapesLeadingAndTrailingSpaces()
    {
        Assert.Equal("\\ admin\\ ", LdapAuthService.EscapeForLdapDN(" admin "));
    }

    [Fact]
    public void EscapeForLdapDN_DoesNotEscapeMiddleSpaces()
    {
        Assert.Equal("ad min", LdapAuthService.EscapeForLdapDN("ad min"));
    }

    [Theory]
    [InlineData("\\", "\\5c")]
    [InlineData("*", "\\2a")]
    [InlineData("(", "\\28")]
    [InlineData(")", "\\29")]
    [InlineData("\0", "\\00")]
    [InlineData("user*name", "user\\2aname")]
    [InlineData("(cn=admin)", "\\28cn=admin\\29")]
    public void EscapeForLdapFilter_EscapesSpecialCharacters(string input, string expected)
    {
        var result = LdapAuthService.EscapeForLdapFilter(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EscapeForLdapFilter_LeavesNormalTextUnchanged()
    {
        Assert.Equal("normaluser", LdapAuthService.EscapeForLdapFilter("normaluser"));
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsFalse_WhenAddressEmpty()
    {
        var config = new SyncConfiguration { LdapAddress = "" };
        var monitor = Mock.Of<IOptionsMonitor<SyncConfiguration>>(m => m.CurrentValue == config);
        var logger = Mock.Of<ILogger<LdapAuthService>>();
        var service = new LdapAuthService(monitor, logger);

        var result = await service.AuthenticateAsync("user", "pass");

        Assert.False(result);
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsFalse_WhenUsernameEmpty()
    {
        var config = new SyncConfiguration { LdapAddress = "ldap://localhost:389" };
        var monitor = Mock.Of<IOptionsMonitor<SyncConfiguration>>(m => m.CurrentValue == config);
        var logger = Mock.Of<ILogger<LdapAuthService>>();
        var service = new LdapAuthService(monitor, logger);

        var result = await service.AuthenticateAsync("", "pass");

        Assert.False(result);
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsFalse_WhenPasswordEmpty()
    {
        var config = new SyncConfiguration { LdapAddress = "ldap://localhost:389" };
        var monitor = Mock.Of<IOptionsMonitor<SyncConfiguration>>(m => m.CurrentValue == config);
        var logger = Mock.Of<ILogger<LdapAuthService>>();
        var service = new LdapAuthService(monitor, logger);

        var result = await service.AuthenticateAsync("user", "");

        Assert.False(result);
    }
}
