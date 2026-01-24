using System.Text;
using System.IO;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Syncthing-compatible local discovery protocol implementation
/// Based on discoproto.Announce protobuf message structure
///
/// VERIFIED AGAINST SYNCTHING SOURCE: lib/discover/local.go (2025-01-25)
/// - Syncthing uses: pkt.Id = c.myID[:] (32 raw bytes, not base32 string)
/// - This implementation correctly converts device ID to/from 32 raw bytes for wire format
///
/// Device ID encoding follows Syncthing protocol:
/// - Wire format: 32 raw bytes (SHA-256 hash of TLS certificate)
/// - Human-readable format: RFC 4648 base32 (A-Z, 2-7) with Luhn32 check digits
/// - String format: 8 groups of 7 chars separated by hyphens (63 chars total)
///
/// IMPORTANT: Device ID MUST be transmitted as 32 raw bytes in the protobuf 'id' field,
/// NOT as a base32-encoded string. This ensures compatibility with native Syncthing clients.
/// </summary>
public static class DiscoveryProtocol
{
    // Base32 alphabet per RFC 4648 (same as Syncthing)
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // Device ID length constants
    private const int RawDeviceIdLength = 32; // 256-bit SHA-256 hash
    private const int Base32DeviceIdLength = 52; // 32 bytes * 8 bits / 5 bits per base32 char
    private const int LuhnifiedLength = 56; // 52 base32 + 4 Luhn check digits (one per 13-char group)
    private const int FormattedLength = 63; // 56 chars + 7 hyphens

    /// <summary>
    /// Creates a Syncthing-compatible discovery announcement packet
    /// Compatible with discoproto.Announce protobuf message format
    /// </summary>
    public static byte[] CreateAnnouncePacket(string deviceId, List<string> addresses, long instanceId)
    {
        using var stream = new MemoryStream();
        
        // Field 1: id (bytes) - tag = (1 << 3) | 2 = 10 (0x0A)
        var deviceIdBytes = ParseDeviceId(deviceId);
        WriteField(stream, 1, 2, deviceIdBytes);
        
        // Field 2: addresses (repeated string) - tag = (2 << 3) | 2 = 18 (0x12)  
        foreach (var address in addresses)
        {
            var addressBytes = Encoding.UTF8.GetBytes(address);
            WriteField(stream, 2, 2, addressBytes);
        }
        
        // Field 3: instance_id (int64) - tag = (3 << 3) | 0 = 24 (0x18)
        WriteVarint64Field(stream, 3, instanceId);
        
        return stream.ToArray();
    }
    
    /// <summary>
    /// Parses a Syncthing-compatible discovery announcement packet
    /// </summary>
    public static DiscoveryAnnouncement? ParseAnnouncePacket(byte[] data)
    {
        if (data.Length == 0) return null;
        
        try
        {
            var announcement = new DiscoveryAnnouncement();
            var stream = new MemoryStream(data);
            
            while (stream.Position < stream.Length)
            {
                var tag = ReadVarint32(stream);
                var fieldNumber = tag >> 3;
                var wireType = (int)(tag & 0x7);
                
                switch (fieldNumber)
                {
                    case 1: // id (bytes)
                        if (wireType == 2)
                        {
                            var length = (int)ReadVarint32(stream);
                            var deviceIdBytes = new byte[length];
                            stream.Read(deviceIdBytes, 0, length);
                            announcement.DeviceId = FormatDeviceId(deviceIdBytes);
                        }
                        break;
                        
                    case 2: // addresses (repeated string)
                        if (wireType == 2)
                        {
                            var length = (int)ReadVarint32(stream);
                            var addressBytes = new byte[length];
                            stream.Read(addressBytes, 0, length);
                            announcement.Addresses.Add(Encoding.UTF8.GetString(addressBytes));
                        }
                        break;
                        
                    case 3: // instance_id (int64)
                        if (wireType == 0)
                        {
                            announcement.InstanceId = ReadVarint64(stream);
                        }
                        break;
                        
                    default:
                        // Skip unknown fields
                        SkipField(stream, wireType);
                        break;
                }
            }
            
            return announcement;
        }
        catch
        {
            return null;
        }
    }
    
    private static void WriteField(Stream stream, int fieldNumber, int wireType, byte[] data)
    {
        var tag = (fieldNumber << 3) | wireType;
        WriteVarint32(stream, (uint)tag);
        
        if (wireType == 2) // Length-delimited
        {
            WriteVarint32(stream, (uint)data.Length);
        }
        
        stream.Write(data, 0, data.Length);
    }
    
    private static void WriteVarint64Field(Stream stream, int fieldNumber, long value)
    {
        var tag = (fieldNumber << 3) | 0; // Wire type 0 for varint
        WriteVarint32(stream, (uint)tag);
        WriteVarint64(stream, (ulong)value);
    }
    
    private static void WriteVarint32(Stream stream, uint value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
    
    private static void WriteVarint64(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
    
    private static uint ReadVarint32(Stream stream)
    {
        uint result = 0;
        int shift = 0;
        
        while (true)
        {
            var b = stream.ReadByte();
            if (b == -1) throw new EndOfStreamException();
            
            result |= (uint)((b & 0x7F) << shift);
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        
        return result;
    }
    
    private static long ReadVarint64(Stream stream)
    {
        long result = 0;
        int shift = 0;
        
        while (true)
        {
            var b = stream.ReadByte();
            if (b == -1) throw new EndOfStreamException();
            
            result |= (long)((b & 0x7F) << shift);
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        
        return result;
    }
    
    private static void SkipField(Stream stream, int wireType)
    {
        switch (wireType)
        {
            case 0: // Varint
                ReadVarint64(stream);
                break;
            case 1: // Fixed64
                stream.Seek(8, SeekOrigin.Current);
                break;
            case 2: // Length-delimited
                var length = (int)ReadVarint32(stream);
                stream.Seek(length, SeekOrigin.Current);
                break;
            case 5: // Fixed32
                stream.Seek(4, SeekOrigin.Current);
                break;
        }
    }
    
    /// <summary>
    /// Parses Syncthing device ID string (Luhnified base32) to raw 32 bytes for wire transmission.
    ///
    /// VERIFIED: This method converts human-readable device ID to raw bytes for the wire format.
    /// Syncthing transmits device IDs as 32 raw bytes (not base32 string) in local discovery.
    /// Reference: lib/discover/local.go - pkt.Id = c.myID[:] where myID is [32]byte
    ///
    /// Input format: AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH (63 chars)
    /// Output: 32 raw bytes (256-bit SHA-256 hash)
    /// </summary>
    private static byte[] ParseDeviceId(string deviceId)
    {
        // Normalize: remove hyphens/spaces, uppercase, fix common typos
        var normalized = deviceId.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        normalized = FixTypos(normalized);

        if (normalized.Length != LuhnifiedLength)
        {
            throw new ArgumentException($"Invalid device ID length: {normalized.Length}, expected {LuhnifiedLength}");
        }

        // Remove Luhn check digits (convert 56 chars to 52 chars)
        var base32 = Unluhnify(normalized);
        if (base32 == null)
        {
            throw new ArgumentException("Invalid device ID Luhn checksum");
        }

        // Decode base32 to raw 32 bytes
        return DecodeBase32(base32);
    }

    /// <summary>
    /// Formats raw 32 device ID bytes to Syncthing string format (Luhnified base32)
    /// </summary>
    private static string FormatDeviceId(byte[] bytes)
    {
        if (bytes.Length != RawDeviceIdLength)
        {
            throw new ArgumentException($"Invalid device ID byte length: {bytes.Length}, expected {RawDeviceIdLength}");
        }

        // Encode 32 bytes as base32 (52 chars)
        var base32 = EncodeBase32(bytes);

        // Add Luhn check digits (52 -> 56 chars)
        var luhnified = Luhnify(base32);

        // Format with hyphens (56 -> 63 chars)
        return Chunkify(luhnified);
    }

    /// <summary>
    /// Encode bytes as RFC 4648 base32 (Syncthing alphabet)
    /// </summary>
    private static string EncodeBase32(byte[] data)
    {
        var result = new StringBuilder();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Decode base32 string to bytes (RFC 4648, Syncthing alphabet)
    /// </summary>
    private static byte[] DecodeBase32(string base32)
    {
        var result = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in base32)
        {
            var value = Base32Codepoint(c);
            if (value < 0)
                throw new ArgumentException($"Invalid base32 character: {c}");

            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                result.Add((byte)(buffer >> (bitsLeft - 8)));
                bitsLeft -= 8;
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Get base32 codepoint value (0-31) for a character
    /// </summary>
    private static int Base32Codepoint(char c)
    {
        if (c >= 'A' && c <= 'Z')
            return c - 'A';
        if (c >= '2' && c <= '7')
            return c - '2' + 26;
        return -1;
    }

    /// <summary>
    /// Add Luhn check digits: split 52 chars into 4 groups of 13, add check digit after each
    /// Result is 56 characters
    /// </summary>
    private static string Luhnify(string s)
    {
        if (s.Length != Base32DeviceIdLength)
            throw new ArgumentException($"Input must be {Base32DeviceIdLength} characters, got {s.Length}");

        var result = new StringBuilder(LuhnifiedLength);
        for (int i = 0; i < 4; i++)
        {
            var group = s.Substring(i * 13, 13);
            result.Append(group);
            result.Append(CalculateLuhn32(group));
        }
        return result.ToString();
    }

    /// <summary>
    /// Remove and validate Luhn check digits: convert 56 chars back to 52 chars
    /// Returns null if validation fails
    /// </summary>
    private static string? Unluhnify(string s)
    {
        if (s.Length != LuhnifiedLength)
            return null;

        var result = new StringBuilder(Base32DeviceIdLength);
        for (int i = 0; i < 4; i++)
        {
            var group = s.Substring(i * 14, 13);
            var checkDigit = s[i * 14 + 13];
            var expectedCheckDigit = CalculateLuhn32(group);

            if (checkDigit != expectedCheckDigit)
                return null;

            result.Append(group);
        }
        return result.ToString();
    }

    /// <summary>
    /// Calculate Luhn32 check digit (following Syncthing lib/protocol/luhn.go)
    /// </summary>
    private static char CalculateLuhn32(string s)
    {
        const int n = 32;
        var factor = 1;
        var sum = 0;

        foreach (var c in s)
        {
            var codepoint = Base32Codepoint(c);
            if (codepoint < 0)
                throw new ArgumentException($"Invalid base32 character: {c}");

            var addend = factor * codepoint;
            factor = (factor == 2) ? 1 : 2;
            addend = (addend / n) + (addend % n);
            sum += addend;
        }

        var remainder = sum % n;
        var checkCodepoint = (n - remainder) % n;
        return Base32Alphabet[checkCodepoint];
    }

    /// <summary>
    /// Format 56-char string with hyphens every 7 characters (Syncthing chunkify)
    /// Result: AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH
    /// </summary>
    private static string Chunkify(string s)
    {
        if (s.Length != LuhnifiedLength)
            return s;

        var chunks = new StringBuilder(FormattedLength);
        for (int i = 0; i < 8; i++)
        {
            if (i > 0)
                chunks.Append('-');
            chunks.Append(s.Substring(i * 7, 7));
        }
        return chunks.ToString();
    }

    /// <summary>
    /// Fix common typing errors in device IDs (0->O, 1->I, 8->B)
    /// </summary>
    private static string FixTypos(string s)
    {
        return s.Replace("0", "O").Replace("1", "I").Replace("8", "B");
    }
}

/// <summary>
/// Discovery announcement message structure
/// </summary>
public class DiscoveryAnnouncement
{
    public string DeviceId { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public long InstanceId { get; set; }
}