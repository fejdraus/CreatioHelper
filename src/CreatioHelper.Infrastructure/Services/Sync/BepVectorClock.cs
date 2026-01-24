using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Vector clock implementation according to Syncthing BEP protocol.
///
/// A vector clock maintains a set of (device_short_id, counter) pairs where:
/// - device_short_id: First 8 bytes of the 32-byte device ID, interpreted as big-endian uint64
/// - counter: Logical timestamp using max(current+1, unix_timestamp) for monotonic progression
///
/// The format matches Syncthing's lib/protocol/vector.go implementation.
/// Counters are kept sorted by device_short_id for deterministic comparison and serialization.
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
    /// Creates a vector clock from BEP wire format.
    /// Parses the (device_short_id, counter_value) pairs from the wire format.
    /// Matches Syncthing's VectorFromWire() in lib/protocol/vector.go.
    /// </summary>
    /// <param name="vector">BEP vector from wire protocol</param>
    public BepVectorClock(BepVector vector)
    {
        foreach (var counter in vector.Counters)
        {
            _counters[counter.Id] = counter.Value;
        }
    }

    /// <summary>
    /// Creates a vector clock from a dictionary of (device_short_id, counter_value) pairs.
    /// </summary>
    /// <param name="counters">Dictionary mapping device_short_id to counter_value</param>
    public BepVectorClock(Dictionary<ulong, ulong> counters)
    {
        _counters = new Dictionary<ulong, ulong>(counters);
    }

    /// <summary>
    /// Updates the vector clock for a device, keeping the maximum value.
    /// This is part of the (device_short_id, counter) pair where:
    /// - deviceShortId: First 8 bytes of device ID as big-endian uint64
    /// - value: Counter value (logical timestamp)
    /// </summary>
    /// <param name="deviceShortId">Short device ID (first 8 bytes of device ID)</param>
    /// <param name="value">Counter value to set (will only update if greater than current)</param>
    public void Update(ulong deviceShortId, ulong value)
    {
        _counters[deviceShortId] = Math.Max(_counters.GetValueOrDefault(deviceShortId), value);
    }

    /// <summary>
    /// Increments the counter for a device using max(current+1, unix_timestamp).
    /// This ensures monotonic progression even with clock skew.
    /// Matches Syncthing's Vector.Update() in lib/protocol/vector.go.
    /// </summary>
    /// <param name="deviceShortId">Short device ID (first 8 bytes of device ID as big-endian uint64)</param>
    public void Increment(ulong deviceShortId)
    {
        var currentValue = _counters.GetValueOrDefault(deviceShortId);
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var newValue = Math.Max(currentValue + 1, timestamp);
        _counters[deviceShortId] = newValue;
    }

    /// <summary>
    /// Merges another vector clock into this one.
    /// Takes the maximum counter value for each device_short_id.
    /// Matches Syncthing's Vector.Merge() in lib/protocol/vector.go.
    /// </summary>
    /// <param name="other">Vector clock to merge</param>
    public void Merge(BepVectorClock other)
    {
        foreach (var (deviceShortId, value) in other._counters)
        {
            _counters[deviceShortId] = Math.Max(_counters.GetValueOrDefault(deviceShortId), value);
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
    /// Converts to BEP wire format for protocol transmission.
    /// Each counter is a (device_short_id, counter_value) pair.
    /// Counters are sorted by device_short_id for deterministic ordering.
    /// Matches Syncthing's Vector.ToWire() in lib/protocol/vector.go.
    /// </summary>
    /// <returns>BepVector containing sorted list of BepCounter (Id, Value) pairs</returns>
    public BepVector ToBepVector()
    {
        var counters = _counters
            .OrderBy(kvp => kvp.Key) // Sort by device_short_id for deterministic ordering
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
    /// Converts device ID string (hex) to short ID for vector clocks.
    /// Uses first 8 bytes of the 32-byte device ID, interpreted as big-endian uint64.
    /// Matches Syncthing's DeviceID.Short() in lib/protocol/deviceid.go.
    /// </summary>
    /// <param name="deviceIdHex">64-character hex string (32 bytes) of device ID</param>
    /// <returns>Short ID as uint64</returns>
    public static ulong DeviceIdToShortId(string deviceIdHex)
    {
        var deviceIdBytes = Convert.FromHexString(deviceIdHex);
        if (deviceIdBytes.Length < 8)
        {
            throw new ArgumentException("Device ID must be at least 8 bytes", nameof(deviceIdHex));
        }
        return BinaryPrimitives.ReadUInt64BigEndian(deviceIdBytes.AsSpan(0, 8));
    }

    /// <summary>
    /// Converts short ID back to hex string (first 8 bytes of device ID).
    /// Returns lowercase hex representation matching Syncthing format.
    /// </summary>
    public static string ShortIdToHex(ulong shortId)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, shortId);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Alias for ShortIdToHex for backwards compatibility.
    /// </summary>
    public static string ShortIdToPartialDeviceId(ulong shortId) => ShortIdToHex(shortId);

    /// <summary>
    /// Parses a vector clock from its string representation.
    /// Format: "hex_shortid:counter,hex_shortid:counter,..."
    /// Matches Syncthing's VectorFromString in lib/protocol/vector.go
    /// </summary>
    /// <param name="s">String representation of vector clock</param>
    /// <returns>Parsed vector clock</returns>
    /// <exception cref="FormatException">If the string format is invalid</exception>
    public static BepVectorClock FromString(string s)
    {
        var vectorClock = new BepVectorClock();

        if (string.IsNullOrWhiteSpace(s))
        {
            return vectorClock;
        }

        var pairs = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var colonIndex = pair.IndexOf(':');
            if (colonIndex == -1)
            {
                throw new FormatException($"Invalid pair format (missing colon): \"{pair}\"");
            }

            var idStr = pair.Substring(0, colonIndex).Trim();
            var valStr = pair.Substring(colonIndex + 1).Trim();

            // Parse hex ID to ulong (big-endian, pad to 8 bytes if needed)
            if (idStr.Length > 16)
            {
                throw new FormatException($"Invalid short ID (too long): \"{idStr}\"");
            }

            // Pad to 16 hex chars (8 bytes) from the left
            var paddedId = idStr.PadLeft(16, '0');
            if (!ulong.TryParse(paddedId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shortId))
            {
                throw new FormatException($"Invalid hex short ID: \"{idStr}\"");
            }

            if (!ulong.TryParse(valStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException($"Invalid counter value: \"{valStr}\"");
            }

            vectorClock._counters[shortId] = value;
        }

        return vectorClock;
    }

    /// <summary>
    /// Returns the string representation of the vector clock.
    /// Format: "hex_shortid:counter,hex_shortid:counter,..."
    /// Matches Syncthing's Vector.String() in lib/protocol/vector.go
    /// </summary>
    public override string ToString()
    {
        if (_counters.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        var first = true;
        foreach (var (shortId, value) in _counters.OrderBy(kvp => kvp.Key))
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            sb.Append(ShortIdToHex(shortId));
            sb.Append(':');
            sb.Append(value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns a human-readable string representation.
    /// Format: "[shortid:counter, shortid:counter, ...]"
    /// </summary>
    public string ToHumanString()
    {
        var parts = _counters
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => $"{ShortIdToHex(kvp.Key)}:{kvp.Value}");
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