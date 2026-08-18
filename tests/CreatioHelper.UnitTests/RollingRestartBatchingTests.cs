using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CreatioHelper.UnitTests;

public class RollingRestartBatchingTests
{
    private static List<List<string>> Split(int serverCount, int requestedBatchSize)
    {
        var servers = Enumerable.Range(1, serverCount).Select(i => $"APP{i:00}").ToList();
        var batchSize = Math.Clamp(requestedBatchSize, 1, Math.Max(servers.Count, 1));

        var batches = new List<List<string>>();
        for (var index = 0; index < servers.Count; index += batchSize)
        {
            batches.Add(servers.Skip(index).Take(batchSize).ToList());
        }

        return batches;
    }

    [Fact]
    public void FourServersInPairs()
    {
        var batches = Split(4, 2);

        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { "APP01", "APP02" }, batches[0]);
        Assert.Equal(new[] { "APP03", "APP04" }, batches[1]);
    }

    [Fact]
    public void LastBatchTakesTheRemainder()
    {
        var batches = Split(5, 2);

        Assert.Equal(3, batches.Count);
        Assert.Single(batches[2]);
        Assert.Equal("APP05", batches[2][0]);
    }

    [Fact]
    public void EveryServerAppearsExactlyOnce()
    {
        var flattened = Split(7, 3).SelectMany(b => b).ToList();

        Assert.Equal(7, flattened.Count);
        Assert.Equal(flattened.Distinct().Count(), flattened.Count);
    }

    [Fact]
    public void BatchLargerThanTheListRestartsEverythingAtOnce()
    {
        var batches = Split(3, 10);

        Assert.Single(batches);
        Assert.Equal(3, batches[0].Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveSizeFallsBackToOneAtATime(int requested)
    {
        var batches = Split(3, requested);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Single(b));
    }

    [Fact]
    public void SingleServerYieldsOneBatch()
    {
        Assert.Single(Split(1, 3));
    }

    [Fact]
    public void OrderIsPreserved()
    {
        var flattened = Split(6, 2).SelectMany(b => b).ToList();

        Assert.Equal(new[] { "APP01", "APP02", "APP03", "APP04", "APP05", "APP06" }, flattened);
    }
}
