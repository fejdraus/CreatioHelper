using CreatioHelper.Domain.Entities;
using Xunit;

namespace CreatioHelper.UnitTests;

public class BepVectorClockPersistenceTests
{
    [Fact]
    public void ToString_FromString_RoundTrips()
    {
        var clock = new BepVectorClock();
        clock.Increment(BepVectorClock.ShortIdFromString("device-a"));
        clock.Increment(BepVectorClock.ShortIdFromString("device-b"));

        var restored = BepVectorClock.FromString(clock.ToString());

        Assert.Equal(clock, restored);
        Assert.Equal(VectorClockComparison.Equal, clock.Compare(restored));
    }

    [Fact]
    public void ParseOrEmpty_ReturnsEmpty_OnLegacyHashValue()
    {
        // Legacy rows stored a file hash (no "id:counter" structure) in version_vector.
        var clock = BepVectorClock.ParseOrEmpty("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4");

        Assert.Empty(clock.Counters);
    }

    [Fact]
    public void ParseOrEmpty_ReturnsEmpty_OnNullOrBlank()
    {
        Assert.Empty(BepVectorClock.ParseOrEmpty(null).Counters);
        Assert.Empty(BepVectorClock.ParseOrEmpty("").Counters);
    }

    [Fact]
    public void ShortIdFromString_IsDeterministic()
    {
        Assert.Equal(
            BepVectorClock.ShortIdFromString("some-device-id"),
            BepVectorClock.ShortIdFromString("some-device-id"));
    }

    [Fact]
    public void Concurrent_Detected_WhenNeitherStrictlyNewer()
    {
        var a = new BepVectorClock();
        a.Increment(BepVectorClock.ShortIdFromString("dev-a"));
        var b = new BepVectorClock();
        b.Increment(BepVectorClock.ShortIdFromString("dev-b"));

        Assert.True(a.IsConcurrentWith(b));
        Assert.Equal(VectorClockComparison.Concurrent, a.Compare(b));
    }

    [Fact]
    public void Merge_ThenIncrement_IsNewerThanBoth()
    {
        var local = new BepVectorClock();
        local.Increment(BepVectorClock.ShortIdFromString("dev-a"));
        var remote = new BepVectorClock();
        remote.Increment(BepVectorClock.ShortIdFromString("dev-b"));

        var merged = local.Copy();
        merged.Merge(remote);
        merged.Increment(BepVectorClock.ShortIdFromString("dev-a"));

        Assert.True(merged.IsNewerThan(local));
        Assert.True(merged.IsNewerThan(remote));
    }
}
