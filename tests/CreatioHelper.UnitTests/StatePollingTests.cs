using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Shared.Utils;
using Xunit;

namespace CreatioHelper.UnitTests;

public class StatePollingTests
{
    private static readonly TimeSpan NoWait = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task ReturnsImmediately_WhenStateAlreadyMatches()
    {
        var reads = 0;

        var reached = await StatePolling.WaitForStateAsync(
            _ => { reads++; return Task.FromResult<string?>("Started"); },
            "Started",
            interval: NoWait);

        Assert.True(reached);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task PollsUntilTheStateChanges()
    {
        var states = new Queue<string?>(new[] { "Starting", "Starting", "Started" });

        var reached = await StatePolling.WaitForStateAsync(
            _ => Task.FromResult(states.Dequeue()),
            "Started",
            interval: NoWait);

        Assert.True(reached);
        Assert.Empty(states);
    }

    [Fact]
    public async Task MatchesStateCaseInsensitively()
    {
        var reached = await StatePolling.WaitForStateAsync(
            _ => Task.FromResult<string?>("STARTED"),
            "Started",
            interval: NoWait);

        Assert.True(reached);
    }

    [Fact]
    public async Task GivesUpAfterTheAllowedAttempts()
    {
        var reads = 0;

        var reached = await StatePolling.WaitForStateAsync(
            _ => { reads++; return Task.FromResult<string?>("Stopped"); },
            "Started",
            interval: NoWait,
            maxAttempts: 3);

        Assert.False(reached);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task ReportsEveryMissedStateToTheCaller()
    {
        var seen = new List<string?>();
        var states = new Queue<string?>(new[] { "Stopping", null, "Started" });

        await StatePolling.WaitForStateAsync(
            _ => Task.FromResult(states.Dequeue()),
            "Started",
            currentState => seen.Add(currentState),
            interval: NoWait);

        Assert.Equal(new string?[] { "Stopping", null }, seen);
    }

    [Fact]
    public async Task StopsWhenCancelledBeforeTheFirstRead()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reads = 0;

        var reached = await StatePolling.WaitForStateAsync(
            _ => { reads++; return Task.FromResult<string?>("Started"); },
            "Started",
            interval: NoWait,
            cancellationToken: cts.Token);

        Assert.False(reached);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task RejectsAnEmptyDesiredState()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            StatePolling.WaitForStateAsync(_ => Task.FromResult<string?>("Started"), "", interval: NoWait));
    }
}
