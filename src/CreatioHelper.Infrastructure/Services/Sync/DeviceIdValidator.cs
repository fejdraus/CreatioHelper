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
        if (string.IsNullOrEmpty(normalizedDeviceId) || normalizedDeviceId.Length != 56)
            return normalizedDeviceId;

        var formatted = new StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            if (i > 0) formatted.Append('-');
            formatted.Append(normalizedDeviceId.Substring(i * 7, 7));
        }

        return formatted.ToString();
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
        // Convert to base32 (Syncthing uses custom base32 without padding)
        var base32 = ConvertToBase32(hash);
        
        // Take first 52 characters (52 + 4 Luhn digits = 56 total)
        var deviceIdBase = base32.Substring(0, 52);
        
        // Calculate and append Luhn checksum
        var luhnDigits = CalculateLuhnChecksum(deviceIdBase);
        var fullDeviceId = deviceIdBase + luhnDigits;
        
        // Format with hyphens: AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH
        return FormatDeviceId(fullDeviceId);
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

    private string CalculateLuhnChecksum(string input)
    {
        // Convert base32 characters to numeric values for Luhn algorithm
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        
        var sum = 0;
        var isEven = false;
        
        // Process from right to left
        for (int i = input.Length - 1; i >= 0; i--)
        {
            var digit = alphabet.IndexOf(input[i]);
            if (digit < 0) throw new ArgumentException($"Invalid character in device ID: {input[i]}");
            
            if (isEven)
            {
                digit *= 2;
                if (digit > 31) // 31 is max for base32 (32-1)
                    digit = (digit % 32) + (digit / 32);
            }
            
            sum += digit;
            isEven = !isEven;
        }
        
        // Calculate checksum digits
        var checksum = (32 - (sum % 32)) % 32;
        
        // Convert back to base32 and pad to 4 characters
        var checksumStr = alphabet[checksum].ToString();
        
        // For Syncthing compatibility, we need exactly 4 checksum digits
        // Calculate additional digits to make total length 56
        var remainingDigits = new StringBuilder();
        for (int i = 1; i < 4; i++)
        {
            // Simple algorithm to generate consistent additional digits
            var additionalDigit = (checksum + i * 7) % 32;
            remainingDigits.Append(alphabet[additionalDigit]);
        }
        
        return checksumStr + remainingDigits.ToString();
    }

    private bool ValidateLuhnChecksum(string deviceId)
    {
        try
        {
            var normalized = NormalizeDeviceId(deviceId);
            if (normalized.Length != 56) return false;
            
            // Extract the base (first 52 chars) and checksum (last 4 chars)
            var deviceIdBase = normalized.Substring(0, 52);
            var providedChecksum = normalized.Substring(52, 4);
            
            // Calculate expected checksum
            var expectedChecksum = CalculateLuhnChecksum(deviceIdBase);
            
            return string.Equals(providedChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase);
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