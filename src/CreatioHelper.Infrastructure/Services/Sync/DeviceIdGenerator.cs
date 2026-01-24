using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Device ID generator - creates unique device identifiers from certificates
/// 100% compatible with Syncthing's protocol.NewDeviceID implementation
/// Uses Base32 encoding (RFC 4648) with Luhn mod N checksum
/// </summary>
public static class DeviceIdGenerator
{
    // Base32 alphabet (RFC 4648 without padding)
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // Luhn alphabet (same as Base32 for Syncthing)
    private const string LuhnAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Generate a device ID from a certificate (SHA256 hash of raw certificate bytes)
    /// Equivalent to Syncthing's protocol.NewDeviceID(rawCert []byte)
    /// </summary>
    public static string GenerateFromCertificate(X509Certificate2 certificate)
    {
        if (certificate == null)
            throw new ArgumentNullException(nameof(certificate));

        var rawCertBytes = certificate.GetRawCertData();
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(rawCertBytes);

        return FormatDeviceId(hash);
    }

    /// <summary>
    /// Generate a device ID from raw certificate bytes
    /// </summary>
    public static string GenerateFromRawBytes(byte[] rawCertBytes)
    {
        if (rawCertBytes == null || rawCertBytes.Length == 0)
            throw new ArgumentException("Raw certificate bytes cannot be null or empty", nameof(rawCertBytes));

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(rawCertBytes);
        return FormatDeviceId(hash);
    }

    /// <summary>
    /// Format device ID hash as Syncthing-compatible string
    /// Uses Base32 encoding with Luhn32 check digits
    /// Output: 8 groups of 7 chars separated by dashes (XXXXXXX-XXXXXXX-...-XXXXXXX)
    /// </summary>
    public static string FormatDeviceId(byte[] hash)
    {
        // Encode to Base32 (52 chars for 32 bytes)
        var base32 = ToBase32(hash);

        // Add Luhn checksum after each group of 13 chars (results in 56 chars total)
        var withChecksums = AddLuhnChecksums(base32);

        // Chunk into 8 groups of 7 chars separated by dashes
        return Chunkify(withChecksums);
    }

    /// <summary>
    /// Encode bytes to Base32 (RFC 4648)
    /// </summary>
    private static string ToBase32(byte[] data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                int index = (buffer >> (bitsLeft - 5)) & 0x1F;
                result.Append(Base32Alphabet[index]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            int index = (buffer << (5 - bitsLeft)) & 0x1F;
            result.Append(Base32Alphabet[index]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Decode Base32 string to bytes, stripping Luhn check digits
    /// Check digits are at positions 13, 27, 41, 55 (0-indexed) in the 56-char string
    /// </summary>
    public static byte[] FromBase32(string base32)
    {
        var cleaned = base32.Replace("-", "").ToUpperInvariant();

        // Strip Luhn check digits at positions 13, 27, 41, 55 (0-indexed)
        var dataOnly = StripLuhnCheckDigits(cleaned);

        var result = new List<byte>();
        int buffer = 0;
        int bitsLeft = 0;

        foreach (char c in dataOnly)
        {
            int value = LuhnAlphabet.IndexOf(c);
            if (value < 0) continue; // Skip invalid chars

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
    /// Strip Luhn check digits from device ID string
    /// Check digits are at positions 13, 27, 41, 55 (0-indexed)
    /// </summary>
    private static string StripLuhnCheckDigits(string input)
    {
        if (input.Length != 56)
            return input; // Not a full device ID, return as-is

        // Positions to skip (check digit positions)
        var checkPositions = new HashSet<int> { 13, 27, 41, 55 };
        var result = new StringBuilder(52);

        for (int i = 0; i < input.Length; i++)
        {
            if (!checkPositions.Contains(i))
            {
                result.Append(input[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Add Luhn check digit after each group of 13 Base32 characters
    /// Syncthing adds check digit at positions 13, 27, 41, 55 (0-indexed: after 13, 26, 39, 52 chars)
    /// </summary>
    private static string AddLuhnChecksums(string base32)
    {
        var result = new StringBuilder();

        for (int i = 0; i < base32.Length; i += 13)
        {
            int length = Math.Min(13, base32.Length - i);
            var chunk = base32.Substring(i, length);
            result.Append(chunk);

            // Add Luhn check digit after each 13-char group
            if (chunk.Length == 13)
            {
                char checkDigit = CalculateLuhn32CheckDigit(result.ToString());
                result.Append(checkDigit);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Calculate Luhn mod N check digit for Base32 string
    /// Based on Syncthing's luhn.go implementation
    /// </summary>
    private static char CalculateLuhn32CheckDigit(string input)
    {
        int n = LuhnAlphabet.Length; // 32
        int factor = 1;
        int sum = 0;

        for (int i = input.Length - 1; i >= 0; i--)
        {
            int codePoint = LuhnAlphabet.IndexOf(input[i]);
            if (codePoint < 0) continue;

            int addend = factor * codePoint;
            factor = factor == 2 ? 1 : 2;
            addend = (addend / n) + (addend % n);
            sum += addend;
        }

        int remainder = sum % n;
        int checkCodePoint = (n - remainder) % n;

        return LuhnAlphabet[checkCodePoint];
    }

    /// <summary>
    /// Split string into chunks of 7 characters separated by dashes
    /// </summary>
    private static string Chunkify(string input)
    {
        var result = new StringBuilder();

        for (int i = 0; i < input.Length; i += 7)
        {
            if (i > 0) result.Append('-');
            int length = Math.Min(7, input.Length - i);
            result.Append(input.Substring(i, length));
        }

        return result.ToString();
    }

    /// <summary>
    /// Validate if a string is a valid Syncthing device ID format
    /// Checks length, characters, and Luhn checksums
    /// </summary>
    public static bool IsValidDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        var cleaned = deviceId.Replace("-", "").Replace(" ", "").ToUpperInvariant();

        // Syncthing device ID is 56 chars (52 data + 4 check digits)
        if (cleaned.Length != 56)
            return false;

        // All characters must be in Base32 alphabet
        if (!cleaned.All(c => LuhnAlphabet.Contains(c)))
            return false;

        // Verify Luhn checksums at positions 13, 27, 41, 55 (1-indexed: 14, 28, 42, 56)
        return VerifyLuhnChecksums(cleaned);
    }

    /// <summary>
    /// Verify Luhn checksums in device ID
    /// </summary>
    private static bool VerifyLuhnChecksums(string cleaned)
    {
        // Check digits are at positions 13, 27, 41, 55 (0-indexed)
        int[] checkPositions = { 13, 27, 41, 55 };

        foreach (int pos in checkPositions)
        {
            if (pos >= cleaned.Length) break;

            var prefix = cleaned.Substring(0, pos);
            char expected = CalculateLuhn32CheckDigit(prefix);

            if (cleaned[pos] != expected)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Get short device ID for display purposes (first 7 characters)
    /// </summary>
    public static string GetShortDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return string.Empty;

        var cleaned = deviceId.Replace("-", "").Replace(" ", "");
        return cleaned.Length >= 7 ? cleaned.Substring(0, 7) : cleaned;
    }

    /// <summary>
    /// Normalize device ID input (handle typos: 0→O, 1→I, 8→B)
    /// Based on Syncthing's untypeoify function
    /// </summary>
    public static string NormalizeDeviceId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        return input.ToUpperInvariant()
            .Replace('0', 'O')
            .Replace('1', 'I')
            .Replace('8', 'B');
    }
}