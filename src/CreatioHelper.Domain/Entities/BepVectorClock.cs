using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CreatioHelper.Domain.Entities;

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

    public BepVectorClock()
    {
    }

    public BepVectorClock(Dictionary<ulong, ulong> counters)
    {
        _counters = new Dictionary<ulong, ulong>(counters);
    }

    public void Update(ulong deviceShortId, ulong value)
    {
        _counters[deviceShortId] = Math.Max(_counters.GetValueOrDefault(deviceShortId), value);
    }

    public void Increment(ulong deviceShortId)
    {
        var currentValue = _counters.GetValueOrDefault(deviceShortId);
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var newValue = Math.Max(currentValue + 1, timestamp);
        _counters[deviceShortId] = newValue;
    }

    public void Merge(BepVectorClock other)
    {
        foreach (var (deviceShortId, value) in other._counters)
        {
            _counters[deviceShortId] = Math.Max(_counters.GetValueOrDefault(deviceShortId), value);
        }
    }

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
        {
            return VectorClockComparison.Greater;
        }

        if (otherGreater && !thisGreater)
        {
            return VectorClockComparison.Lesser;
        }

        if (!thisGreater && !otherGreater)
        {
            return VectorClockComparison.Equal;
        }

        return VectorClockComparison.Concurrent;
    }

    public bool IsNewerThan(BepVectorClock other)
    {
        return Compare(other) == VectorClockComparison.Greater;
    }

    public bool IsOlderThan(BepVectorClock other)
    {
        return Compare(other) == VectorClockComparison.Lesser;
    }

    public bool IsConcurrentWith(BepVectorClock other)
    {
        return Compare(other) == VectorClockComparison.Concurrent;
    }

    public BepVectorClock Copy()
    {
        return new BepVectorClock(new Dictionary<ulong, ulong>(_counters));
    }

    public static string GenerateDeviceId(byte[] certificateBytes)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(certificateBytes);
        return Convert.ToHexString(hash).ToLower();
    }

    public static ulong DeviceIdToShortId(string deviceIdHex)
    {
        var deviceIdBytes = Convert.FromHexString(deviceIdHex);
        if (deviceIdBytes.Length < 8)
        {
            throw new ArgumentException("Device ID must be at least 8 bytes", nameof(deviceIdHex));
        }
        return BinaryPrimitives.ReadUInt64BigEndian(deviceIdBytes.AsSpan(0, 8));
    }

    public static string ShortIdToHex(ulong shortId)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, shortId);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string ShortIdToPartialDeviceId(ulong shortId) => ShortIdToHex(shortId);

    /// <summary>
    /// Derives a deterministic device short id from an arbitrary device identifier.
    /// A 64-char hex device id uses its first 8 bytes (Syncthing semantics); any other
    /// string falls back to the first 8 bytes of its SHA-256 hash. Deterministic across
    /// processes and machines (unlike string.GetHashCode).
    /// </summary>
    public static ulong ShortIdFromString(string deviceId)
    {
        try
        {
            return DeviceIdToShortId(deviceId);
        }
        catch
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId ?? string.Empty));
            return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8));
        }
    }

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

            if (idStr.Length > 16)
            {
                throw new FormatException($"Invalid short ID (too long): \"{idStr}\"");
            }

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
    /// Lenient parse for persistence boundaries: returns an empty clock instead of
    /// throwing on malformed input (e.g. legacy rows that stored a file hash rather
    /// than a serialized vector clock). An empty clock forces a re-pull, which is the
    /// desired behaviour for such rows.
    /// </summary>
    public static BepVectorClock ParseOrEmpty(string? s)
    {
        try
        {
            return FromString(s ?? string.Empty);
        }
        catch (FormatException)
        {
            return new BepVectorClock();
        }
    }

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

    public string ToHumanString()
    {
        var parts = _counters
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => $"{ShortIdToHex(kvp.Key)}:{kvp.Value}");
        return "[" + string.Join(", ", parts) + "]";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not BepVectorClock other)
        {
            return false;
        }

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
    Equal,
    Greater,
    Lesser,
    Concurrent
}
