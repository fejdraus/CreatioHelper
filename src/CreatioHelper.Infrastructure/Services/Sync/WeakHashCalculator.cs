using System.Numerics;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Implements Adler-32 weak hash algorithm used by Syncthing
/// Based on RFC 1950 and Syncthing's implementation
/// </summary>
public static class WeakHashCalculator
{
    private const uint ADLER32_BASE = 65521; // Largest prime smaller than 2^16

    /// <summary>
    /// Calculates Adler-32 weak hash of data block
    /// </summary>
    /// <param name="data">Data to hash</param>
    /// <param name="offset">Offset in data</param>
    /// <param name="length">Length of data to hash</param>
    /// <returns>32-bit Adler-32 hash</returns>
    public static uint CalculateAdler32(byte[] data, int offset = 0, int? length = null)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        var len = length ?? (data.Length - offset);
        if (offset < 0 || offset > data.Length || offset + len > data.Length)
            throw new ArgumentOutOfRangeException();

        uint a = 1; // Adler-32 checksum starts with 1, not 0
        uint b = 0;

        // Process data in chunks to avoid overflow
        int index = offset;
        int remaining = len;

        while (remaining > 0)
        {
            // Process up to 5552 bytes at a time to prevent overflow
            // 5552 is the largest number of consecutive bytes that can be processed 
            // without causing overflow in the 32-bit calculations
            int chunkSize = Math.Min(remaining, 5552);
            int endIndex = index + chunkSize;

            while (index < endIndex)
            {
                a += data[index++];
                b += a;
            }

            // Apply modulo to prevent overflow
            a %= ADLER32_BASE;
            b %= ADLER32_BASE;

            remaining -= chunkSize;
        }

        // Combine high and low parts: (b << 16) | a
        return (b << 16) | a;
    }

    /// <summary>
    /// Calculates Adler-32 weak hash of data span
    /// </summary>
    /// <param name="data">Data span to hash</param>
    /// <returns>32-bit Adler-32 hash</returns>
    public static uint CalculateAdler32(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;

        int index = 0;
        int remaining = data.Length;

        while (remaining > 0)
        {
            int chunkSize = Math.Min(remaining, 5552);
            int endIndex = index + chunkSize;

            while (index < endIndex)
            {
                a += data[index++];
                b += a;
            }

            a %= ADLER32_BASE;
            b %= ADLER32_BASE;

            remaining -= chunkSize;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// Calculates rolling Adler-32 hash using previous hash and sliding window
    /// This is used for efficient delta sync - allows updating hash when sliding
    /// a window over data without recalculating the entire hash
    /// </summary>
    /// <param name="oldHash">Previous hash value</param>
    /// <param name="oldByte">Byte being removed from window</param>
    /// <param name="newByte">Byte being added to window</param>
    /// <param name="windowSize">Size of the sliding window</param>
    /// <returns>Updated Adler-32 hash</returns>
    public static uint RollingAdler32(uint oldHash, byte oldByte, byte newByte, int windowSize)
    {
        // Extract a and b from combined hash
        uint oldA = oldHash & 0xFFFF;
        uint oldB = oldHash >> 16;

        // Update a: subtract old byte, add new byte
        uint newA = (oldA - oldByte + newByte) % ADLER32_BASE;

        // Update b: subtract (windowSize * oldByte), subtract oldA, add newA
        uint newB = (oldB - (uint)windowSize * oldByte - oldA + newA) % ADLER32_BASE;

        // Combine and return
        return (newB << 16) | newA;
    }

    /// <summary>
    /// Validates an Adler-32 hash value
    /// </summary>
    /// <param name="hash">Hash to validate</param>
    /// <returns>True if hash format is valid</returns>
    public static bool IsValidAdler32(uint hash)
    {
        uint a = hash & 0xFFFF;
        uint b = hash >> 16;
        
        // Both parts should be less than ADLER32_BASE
        return a < ADLER32_BASE && b < ADLER32_BASE && a > 0; // a should never be 0 for non-empty data
    }

    /// <summary>
    /// Formats Adler-32 hash for display/logging
    /// </summary>
    /// <param name="hash">Hash value</param>
    /// <returns>Formatted hex string</returns>
    public static string FormatAdler32(uint hash)
    {
        return $"0x{hash:X8}";
    }

    /// <summary>
    /// Compares two Adler-32 hashes for equality
    /// </summary>
    /// <param name="hash1">First hash</param>
    /// <param name="hash2">Second hash</param>
    /// <returns>True if hashes are equal</returns>
    public static bool CompareAdler32(uint hash1, uint hash2)
    {
        return hash1 == hash2;
    }

    /// <summary>
    /// Calculates weak hash for a block of data (combines strong and weak hashing)
    /// This is the main method used by block comparison in Syncthing
    /// </summary>
    /// <param name="data">Block data</param>
    /// <param name="offset">Offset in data</param>
    /// <param name="length">Length of block</param>
    /// <returns>Tuple of (weak hash, strong hash)</returns>
    public static (uint WeakHash, string StrongHash) CalculateBlockHashes(byte[] data, int offset = 0, int? length = null)
    {
        var len = length ?? (data.Length - offset);
        
        // Calculate weak hash (Adler-32)
        var weakHash = CalculateAdler32(data, offset, len);
        
        // Calculate strong hash (SHA-256)
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var strongHashBytes = sha256.ComputeHash(data, offset, len);
        var strongHash = Convert.ToHexString(strongHashBytes).ToLower();
        
        return (weakHash, strongHash);
    }
}