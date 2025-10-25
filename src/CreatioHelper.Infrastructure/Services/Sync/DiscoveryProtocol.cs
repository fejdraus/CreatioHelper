using System.Text;
using System.IO;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Syncthing-compatible local discovery protocol implementation
/// Based on discoproto.Announce protobuf message structure
/// </summary>
public static class DiscoveryProtocol
{
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
    /// Parses Syncthing device ID string to bytes
    /// Device IDs are 32-byte (256-bit) identifiers encoded as hex strings with dashes
    /// </summary>
    private static byte[] ParseDeviceId(string deviceId)
    {
        // Remove dashes and convert to uppercase
        var cleaned = deviceId.Replace("-", "").ToUpperInvariant();
        
        if (cleaned.Length != 64) // 32 bytes * 2 hex chars
        {
            throw new ArgumentException($"Invalid device ID length: {cleaned.Length}, expected 64");
        }
        
        var bytes = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }
        
        return bytes;
    }
    
    /// <summary>
    /// Formats device ID bytes to Syncthing string format
    /// </summary>
    private static string FormatDeviceId(byte[] bytes)
    {
        if (bytes.Length != 32)
        {
            throw new ArgumentException($"Invalid device ID byte length: {bytes.Length}, expected 32");
        }
        
        var hex = Convert.ToHexString(bytes);
        
        // Format with dashes: XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX-XXXXXXX
        var formatted = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 7)
        {
            if (i > 0) formatted.Append('-');
            formatted.Append(hex.Substring(i, Math.Min(7, hex.Length - i)));
        }
        
        return formatted.ToString();
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