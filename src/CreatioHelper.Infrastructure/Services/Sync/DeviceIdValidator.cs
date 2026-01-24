using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Device ID validation and generation following Syncthing protocol
/// Implements Luhn checksum algorithm and proper formatting
/// </summary>
public class DeviceIdValidator
{
    private readonly ILogger<DeviceIdValidator> _logger;
    
    public DeviceIdValidator(ILogger<DeviceIdValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generate Syncthing-compatible Device ID from X.509 certificate
    /// Format: AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH
    /// </summary>
    /// <param name="certificate">X.509 certificate</param>
    /// <returns>Formatted Device ID with Luhn checksum</returns>
    public string GenerateDeviceId(X509Certificate2 certificate)
    {
        try
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(certificate.RawData);
            
            var deviceId = FormatDeviceIdWithLuhn(hash);
            
            _logger.LogInformation("🆔 Generated Device ID: {DeviceId} from certificate {Subject}", 
                deviceId, certificate.Subject);
            
            return deviceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Device ID from certificate {Subject}", certificate.Subject);
            throw;
        }
    }

    /// <summary>
    /// Generate Device ID from raw key material (for testing)
    /// </summary>
    /// <param name="keyMaterial">Raw key bytes</param>
    /// <returns>Formatted Device ID with Luhn checksum</returns>
    public string GenerateDeviceId(byte[] keyMaterial)
    {
        try
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(keyMaterial);
            
            var deviceId = FormatDeviceIdWithLuhn(hash);
            
            _logger.LogDebug("🆔 Generated Device ID: {DeviceId} from key material", deviceId);
            
            return deviceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Device ID from key material");
            throw;
        }
    }

    /// <summary>
    /// Validate Device ID format and Luhn checksum
    /// </summary>
    /// <param name="deviceId">Device ID to validate</param>
    /// <returns>True if Device ID is valid</returns>
    public bool ValidateDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            _logger.LogWarning("⚠️ Device ID is null or empty");
            return false;
        }

        try
        {
            // Check format: AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH
            if (deviceId.Length != 63) // 8 groups of 7 chars + 7 hyphens
            {
                _logger.LogWarning("⚠️ Device ID has invalid length: {Length}, expected 63", deviceId.Length);
                return false;
            }

            var parts = deviceId.Split('-');
            if (parts.Length != 8)
            {
                _logger.LogWarning("⚠️ Device ID has invalid number of parts: {Parts}, expected 8", parts.Length);
                return false;
            }

            // Check each part is 7 characters of valid base32
            foreach (var part in parts)
            {
                if (part.Length != 7)
                {
                    _logger.LogWarning("⚠️ Device ID part has invalid length: {Length}, expected 7", part.Length);
                    return false;
                }

                if (!IsValidBase32(part))
                {
                    _logger.LogWarning("⚠️ Device ID part contains invalid base32 characters: {Part}", part);
                    return false;
                }
            }

            // Validate Luhn checksum
            if (!ValidateLuhnChecksum(deviceId))
            {
                _logger.LogWarning("⚠️ Device ID has invalid Luhn checksum: {DeviceId}", deviceId);
                return false;
            }

            _logger.LogDebug("✅ Device ID validation passed: {DeviceId}", deviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating Device ID: {DeviceId}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// Parse and normalize Device ID (remove hyphens, convert to uppercase)
    /// </summary>
    /// <param name="deviceId">Device ID to normalize</param>
    /// <returns>Normalized Device ID</returns>
    public string NormalizeDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return string.Empty;

        return deviceId.Replace("-", "").ToUpperInvariant();
    }

    /// <summary>
    /// Format normalized Device ID with hyphens
    /// </summary>
    /// <param name="normalizedDeviceId">56-character normalized Device ID</param>
    /// <returns>Formatted Device ID with hyphens</returns>
    public string FormatDeviceId(string normalizedDeviceId)
    {
        return Chunkify(normalizedDeviceId);
    }

    /// <summary>
    /// Check if two Device IDs are equal (ignoring formatting)
    /// </summary>
    /// <param name="deviceId1">First Device ID</param>
    /// <param name="deviceId2">Second Device ID</param>
    /// <returns>True if Device IDs are equal</returns>
    public bool AreEqual(string deviceId1, string deviceId2)
    {
        var normalized1 = NormalizeDeviceId(deviceId1);
        var normalized2 = NormalizeDeviceId(deviceId2);
        
        return string.Equals(normalized1, normalized2, StringComparison.OrdinalIgnoreCase);
    }

    private string FormatDeviceIdWithLuhn(byte[] hash)
    {
        // Convert to base32 (Syncthing uses RFC 4648 base32 without padding)
        var base32 = ConvertToBase32(hash);

        // Take first 52 characters
        var deviceIdBase = base32.Substring(0, 52);

        // Luhnify: split into 4 groups of 13 chars, add check digit after each
        var luhnified = Luhnify(deviceIdBase);

        // Chunkify: format with hyphens every 7 characters
        return Chunkify(luhnified);
    }

    /// <summary>
    /// Add Luhn check digits to a 52-character base32 string.
    /// Splits into 4 groups of 13 chars and adds a check digit after each.
    /// Result is 56 characters.
    /// </summary>
    private string Luhnify(string s)
    {
        if (s.Length != 52)
            throw new ArgumentException($"Input must be 52 characters, got {s.Length}");

        var result = new StringBuilder(56);
        for (int i = 0; i < 4; i++)
        {
            var group = s.Substring(i * 13, 13);
            result.Append(group);
            result.Append(CalculateLuhn32(group));
        }
        return result.ToString();
    }

    /// <summary>
    /// Remove and validate Luhn check digits from a 56-character string.
    /// Returns the original 52-character string if valid.
    /// </summary>
    private string? Unluhnify(string s)
    {
        if (s.Length != 56)
            return null;

        var result = new StringBuilder(52);
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
    /// Format a 56-character string with hyphens every 7 characters.
    /// Result: AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH
    /// </summary>
    private static string Chunkify(string s)
    {
        if (s.Length != 56)
            return s;

        var chunks = new StringBuilder(63); // 56 chars + 7 hyphens
        for (int i = 0; i < 8; i++)
        {
            if (i > 0)
                chunks.Append('-');
            chunks.Append(s.Substring(i * 7, 7));
        }
        return chunks.ToString();
    }

    /// <summary>
    /// Remove hyphens and spaces from a device ID.
    /// </summary>
    private static string Unchunkify(string s)
    {
        return s.Replace("-", "").Replace(" ", "");
    }

    /// <summary>
    /// Replace common typing errors in device IDs (0->O, 1->I, 8->B)
    /// </summary>
    private static string Untypeoify(string s)
    {
        return s.Replace("0", "O").Replace("1", "I").Replace("8", "B");
    }

    private string ConvertToBase32(byte[] data)
    {
        // Syncthing uses RFC 4648 base32 without padding
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        
        var result = new StringBuilder();
        var buffer = 0;
        var bitsLeft = 0;
        
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            
            while (bitsLeft >= 5)
            {
                result.Append(alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }
        
        if (bitsLeft > 0)
        {
            result.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }
        
        return result.ToString();
    }

    /// <summary>
    /// Calculate Luhn32 check digit for a base32 string.
    /// This follows the exact Syncthing algorithm from lib/protocol/luhn.go
    /// </summary>
    /// <param name="s">Base32 string to calculate check digit for</param>
    /// <returns>Check digit character</returns>
    private char CalculateLuhn32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        const int n = 32;

        var factor = 1;
        var sum = 0;

        foreach (var c in s)
        {
            var codepoint = Codepoint32(c);
            if (codepoint == -1)
                throw new ArgumentException($"Character '{c}' not valid in base32 alphabet");

            var addend = factor * codepoint;
            factor = (factor == 2) ? 1 : 2;
            addend = (addend / n) + (addend % n);
            sum += addend;
        }

        var remainder = sum % n;
        var checkCodepoint = (n - remainder) % n;
        return alphabet[checkCodepoint];
    }

    /// <summary>
    /// Convert a base32 character to its codepoint value (0-31)
    /// </summary>
    private static int Codepoint32(char b)
    {
        if (b >= 'A' && b <= 'Z')
            return b - 'A';
        if (b >= '2' && b <= '7')
            return b - '2' + 26;
        return -1;
    }

    private bool ValidateLuhnChecksum(string deviceId)
    {
        try
        {
            // Normalize: uppercase, remove hyphens/spaces, fix typos
            var normalized = Unchunkify(deviceId).ToUpperInvariant();
            normalized = Untypeoify(normalized);

            if (normalized.Length != 56)
                return false;

            // Validate by attempting to unluhnify
            var base52 = Unluhnify(normalized);
            return base52 != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating Luhn checksum for Device ID: {DeviceId}", deviceId);
            return false;
        }
    }

    private bool IsValidBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        return input.All(c => alphabet.Contains(c));
    }
}

/// <summary>
/// Extensions for Device ID operations
/// </summary>
public static class DeviceIdExtensions
{
    /// <summary>
    /// Convert Device ID to short form (first 7 characters)
    /// </summary>
    /// <param name="deviceId">Full Device ID</param>
    /// <returns>Short Device ID</returns>
    public static string ToShortId(this string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return string.Empty;
        
        var normalized = deviceId.Replace("-", "");
        return normalized.Length >= 7 ? normalized.Substring(0, 7) : normalized;
    }

    /// <summary>
    /// Check if Device ID is in short form
    /// </summary>
    /// <param name="deviceId">Device ID to check</param>
    /// <returns>True if Device ID is short form (7 characters)</returns>
    public static bool IsShortId(this string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        
        var normalized = deviceId.Replace("-", "");
        return normalized.Length == 7 && normalized.All(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(c));
    }

    /// <summary>
    /// Get the group index for a Device ID character
    /// </summary>
    /// <param name="deviceId">Device ID</param>
    /// <param name="position">Character position</param>
    /// <returns>Group index (0-7)</returns>
    public static int GetGroupIndex(this string deviceId, int position)
    {
        var normalized = deviceId.Replace("-", "");
        if (position < 0 || position >= normalized.Length) return -1;
        
        return position / 7;
    }
}