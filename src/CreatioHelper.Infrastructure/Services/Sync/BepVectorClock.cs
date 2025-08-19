using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Vector clock implementation according to Syncthing protocol
/// Maintains logical timestamps for conflict resolution
/// </summary>
public class BepVectorClock
{
    private readonly Dictionary<ulong, ulong> _counters = new();

    public IReadOnlyDictionary<ulong, ulong> Counters => _counters;

    /// <summary>
    /// Creates a new vector clock
    /// </summary>
    public BepVectorClock()
    {
    }

    /// <summary>
    /// Creates a vector clock from BEP vector
    /// </summary>
    public BepVectorClock(BepVector vector)
    {
        foreach (var counter in vector.Counters)
        {
            _counters[counter.Id] = counter.Value;
        }
    }

    /// <summary>
    /// Creates a vector clock from counters
    /// </summary>
    public BepVectorClock(Dictionary<ulong, ulong> counters)
    {
        _counters = new Dictionary<ulong, ulong>(counters);
    }

    /// <summary>
    /// Updates the vector clock for a device
    /// </summary>
    public void Update(ulong deviceId, ulong value)
    {
        _counters[deviceId] = Math.Max(_counters.GetValueOrDefault(deviceId), value);
    }

    /// <summary>
    /// Increments the counter for a device
    /// Uses max(current+1, unix_timestamp) as per Syncthing protocol
    /// </summary>
    public void Increment(ulong deviceId)
    {
        var currentValue = _counters.GetValueOrDefault(deviceId);
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var newValue = Math.Max(currentValue + 1, timestamp);
        _counters[deviceId] = newValue;
    }

    /// <summary>
    /// Merges another vector clock into this one
    /// Takes the maximum value for each device
    /// </summary>
    public void Merge(BepVectorClock other)
    {
        foreach (var (deviceId, value) in other._counters)
        {
            _counters[deviceId] = Math.Max(_counters.GetValueOrDefault(deviceId), value);
        }
    }

    /// <summary>
    /// Compares this vector clock with another
    /// </summary>
    public VectorClockComparison Compare(BepVectorClock other)
    {
        var allDevices = _counters.Keys.Union(other._counters.Keys).ToHashSet();
        
        bool thisGreater = false;
        bool otherGreater = false;

        foreach (var deviceId in allDevices)
        {
            var thisValue = _counters.GetValueOrDefault(deviceId);
            var otherValue = other._counters.GetValueOrDefault(deviceId);

            if (thisValue > otherValue)
            {
                thisGreater = true;
            }
            else if (otherValue > thisValue)
            {
                otherGreater = true;
            }
        }

        if (thisGreater && !otherGreater)
            return VectorClockComparison.Greater;
        
        if (otherGreater && !thisGreater)
            return VectorClockComparison.Lesser;
        
        if (!thisGreater && !otherGreater)
            return VectorClockComparison.Equal;
        
        return VectorClockComparison.Concurrent;
    }

    /// <summary>
    /// Checks if this vector clock is newer than another
    /// </summary>
    public bool IsNewerThan(BepVectorClock other)
    {
        return Compare(other) == VectorClockComparison.Greater;
    }

    /// <summary>
    /// Checks if this vector clock is older than another
    /// </summary>
    public bool IsOlderThan(BepVectorClock other)
    {
        return Compare(other) == VectorClockComparison.Lesser;
    }

    /// <summary>
    /// Checks if this vector clock is concurrent with another (conflict)
    /// </summary>
    public bool IsConcurrentWith(BepVectorClock other)
    {
        return Compare(other) == VectorClockComparison.Concurrent;
    }

    /// <summary>
    /// Converts to BEP vector format
    /// </summary>
    public BepVector ToBepVector()
    {
        var counters = _counters
            .OrderBy(kvp => kvp.Key) // Sort by device ID for deterministic ordering
            .Select(kvp => new BepCounter { Id = kvp.Key, Value = kvp.Value })
            .ToList();

        return new BepVector { Counters = counters };
    }

    /// <summary>
    /// Creates a copy of this vector clock
    /// </summary>
    public BepVectorClock Copy()
    {
        return new BepVectorClock(new Dictionary<ulong, ulong>(_counters));
    }

    /// <summary>
    /// Generates device ID from certificate bytes (Syncthing method)
    /// </summary>
    public static string GenerateDeviceId(byte[] certificateBytes)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(certificateBytes);
        return Convert.ToHexString(hash).ToLower();
    }

    /// <summary>
    /// Converts device ID string to short ID for vector clocks
    /// Uses first 8 bytes of device ID as per Syncthing protocol
    /// </summary>
    public static ulong DeviceIdToShortId(string deviceIdHex)
    {
        var deviceIdBytes = Convert.FromHexString(deviceIdHex);
        return BinaryPrimitives.ReadUInt64BigEndian(deviceIdBytes.AsSpan(0, 8));
    }

    /// <summary>
    /// Converts short ID back to device ID (partial)
    /// </summary>
    public static string ShortIdToPartialDeviceId(ulong shortId)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, shortId);
        return Convert.ToHexString(bytes).ToLower();
    }

    public override string ToString()
    {
        var parts = _counters
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => $"{ShortIdToPartialDeviceId(kvp.Key)}:{kvp.Value}");
        return "[" + string.Join(", ", parts) + "]";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not BepVectorClock other) return false;
        
        var allDevices = _counters.Keys.Union(other._counters.Keys);
        return allDevices.All(deviceId => 
            _counters.GetValueOrDefault(deviceId) == other._counters.GetValueOrDefault(deviceId));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (deviceId, value) in _counters.OrderBy(kvp => kvp.Key))
        {
            hash.Add(deviceId);
            hash.Add(value);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// Vector clock comparison result
/// </summary>
public enum VectorClockComparison
{
    /// <summary>
    /// Vector clocks are identical
    /// </summary>
    Equal,
    
    /// <summary>
    /// This vector clock is newer (happens-after)
    /// </summary>
    Greater,
    
    /// <summary>
    /// This vector clock is older (happens-before)
    /// </summary>
    Lesser,
    
    /// <summary>
    /// Vector clocks are concurrent (conflict)
    /// </summary>
    Concurrent
}