using CreatioHelper.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CreatioHelper.Agent.Tests;

public class WebServerPermissionTests
{
    [Theory]
    [InlineData("Import-Module : Необходимо повысить уровень процесса для доступа к данным конфигурации IIS.")]
    [InlineData("Access is denied")]
    [InlineData("Requested registry access is not allowed.")]
    [InlineData("The requested operation requires elevation.")]
    [InlineData("Get-Website : Cannot find a provider with the name 'WebAdministration'.")]
    [InlineData("Get-Website : ProviderNotFound")]
    public void IsPermissionError_DetectsElevationSignatures(string stderr)
    {
        Assert.True(WebServerPermission.IsPermissionError(stderr));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Cannot find path 'IIS:\\Sites\\Nope' because it does not exist.")]
    public void IsPermissionError_IgnoresUnrelatedErrors(string? stderr)
    {
        Assert.False(WebServerPermission.IsPermissionError(stderr));
    }

    [Fact]
    public void AccessStatus_LogsWarningOnlyOnFirstReport()
    {
        var status = new WebServerAccessStatus();

        var firstLogged = status.ReportPermissionIssue("op", "Access is denied", NullLogger.Instance);
        var secondLogged = status.ReportPermissionIssue("op", "Access is denied", NullLogger.Instance);

        Assert.True(firstLogged);
        Assert.False(secondLogged);
        Assert.True(status.RequiresElevation);
        Assert.NotNull(status.Message);
    }

    [Fact]
    public void AccessStatus_ReportSuccessClearsFlagAndReArms()
    {
        var status = new WebServerAccessStatus();

        status.ReportPermissionIssue("op", "Access is denied", NullLogger.Instance);
        status.ReportSuccess();

        Assert.False(status.RequiresElevation);
        Assert.Null(status.Message);

        var loggedAgain = status.ReportPermissionIssue("op", "Access is denied", NullLogger.Instance);
        Assert.True(loggedAgain);
    }
}
