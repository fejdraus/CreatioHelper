using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Wire-format bridge between the domain <see cref="BepVectorClock"/> and the
/// BEP protocol message type <see cref="BepVector"/>. Kept in Infrastructure so
/// the domain clock stays free of protocol/message dependencies.
/// </summary>
public static class BepVectorClockWireExtensions
{
    /// <summary>
    /// Builds a vector clock from the BEP wire representation.
    /// Matches Syncthing's VectorFromWire() in lib/protocol/vector.go.
    /// </summary>
    public static BepVectorClock FromBepVector(BepVector vector)
    {
        var counters = new Dictionary<ulong, ulong>();
        foreach (var counter in vector.Counters)
        {
            counters[counter.Id] = counter.Value;
        }
        return new BepVectorClock(counters);
    }

    /// <summary>
    /// Converts to BEP wire format. Counters are sorted by device_short_id for
    /// deterministic ordering. Matches Syncthing's Vector.ToWire().
    /// </summary>
    public static BepVector ToBepVector(this BepVectorClock clock)
    {
        var counters = clock.Counters
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new BepCounter { Id = kvp.Key, Value = kvp.Value })
            .ToList();

        return new BepVector { Counters = counters };
    }
}
