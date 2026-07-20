using System;
using System.Threading.Tasks;
using CreatioHelper.Shared.Utils;
using Xunit;

namespace CreatioHelper.UnitTests;

public class PowerShellRunnerTests
{
    [Theory]
    [InlineData("Default Web Site", "Default Web Site")]
    [InlineData("O'Brien pool", "O''Brien pool")]
    [InlineData("a'b'c", "a''b''c")]
    [InlineData("", "")]
    public void EscapeSingleQuoted_DoublesApostrophes(string value, string expected)
    {
        Assert.Equal(expected, PowerShellRunner.EscapeSingleQuoted(value));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsLocalServer_RecognisesEveryLocalForm(string? serverName)
    {
        Assert.True(PowerShellRunner.IsLocalServer(serverName));
    }

    [Fact]
    public void IsLocalServer_RecognisesTheMachineNameCaseInsensitively()
    {
        Assert.True(PowerShellRunner.IsLocalServer(Environment.MachineName));
        Assert.True(PowerShellRunner.IsLocalServer(Environment.MachineName.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("SRV-IIS-01")]
    [InlineData("192.168.1.10")]
    public void IsLocalServer_TreatsOtherHostsAsRemote(string serverName)
    {
        Assert.False(PowerShellRunner.IsLocalServer(serverName));
    }

    [Fact]
    public async Task EscapedNameSurvivesAsALiteralArgument()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = "O'Brien";
        var result = await PowerShellRunner.RunAsync(
            $"Write-Output '{PowerShellRunner.EscapeSingleQuoted(name)}'");

        Assert.NotNull(result);
        Assert.False(result!.HasError);
        Assert.Contains("O'Brien", result.Output);
    }

    [Fact]
    public async Task UnescapedApostropheWouldBreakTheCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await PowerShellRunner.RunAsync("Write-Output 'O'Brien'");

        Assert.NotNull(result);
        Assert.True(result!.HasError);
    }

    [Fact]
    public async Task ReturnsOutputAndZeroExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await PowerShellRunner.RunAsync("Write-Output 'hello'");

        Assert.NotNull(result);
        Assert.Equal(0, result!.ExitCode);
        Assert.Contains("hello", result.Output);
        Assert.False(result.HasError);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task CapturesErrorStreamSeparately()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await PowerShellRunner.RunAsync("Write-Error 'boom'");

        Assert.NotNull(result);
        Assert.True(result!.HasError);
        Assert.Contains("boom", result.Error);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ReportsNonZeroExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await PowerShellRunner.RunAsync("exit 3");

        Assert.NotNull(result);
        Assert.Equal(3, result!.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DoesNotDeadlockOnOutputLargerThanThePipeBuffer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var run = PowerShellRunner.RunAsync("1..4000 | ForEach-Object { 'x' * 100 }");
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(run, completed);

        var result = await run;
        Assert.NotNull(result);
        Assert.True(result!.Output.Length > 200_000, $"expected a large payload, got {result.Output.Length} chars");
    }

    [Fact]
    public async Task DrainsBothStreamsWhenBothAreLarge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var run = PowerShellRunner.RunAsync(
            "1..2000 | ForEach-Object { 'o' * 100 }; 1..2000 | ForEach-Object { Write-Error ('e' * 100) }");
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(run, completed);

        var result = await run;
        Assert.NotNull(result);
        Assert.True(result!.Output.Length > 100_000);
        Assert.True(result.Error.Length > 100_000);
    }

    [Fact]
    public async Task Utf8FlagPreservesNonAsciiOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await PowerShellRunner.RunAsync(
            PowerShellRunner.Utf8OutputPrologue + "Write-Output 'привет'",
            useUtf8: true);

        Assert.NotNull(result);
        Assert.Contains("привет", result!.Output);
    }
}
