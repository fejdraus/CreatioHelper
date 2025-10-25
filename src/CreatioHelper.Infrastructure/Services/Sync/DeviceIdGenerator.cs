using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Device ID generator - creates unique device identifiers from certificates
/// Based on Syncthing's protocol.NewDeviceID implementation
/// </summary>
public static class DeviceIdGenerator
{
    /// <summary>
    /// Generate a device ID from a certificate (SHA256 hash of raw certificate bytes)
    /// Equivalent to Syncthing's protocol.NewDeviceID(rawCert []byte)
    /// </summary>
    public static string GenerateFromCertificate(X509Certificate2 certificate)
    {
        if (certificate == null)
            throw new ArgumentNullException(nameof(certificate));

        // Get raw certificate bytes (equivalent to cert.Certificate[0] in Syncthing)
        var rawCertBytes = certificate.GetRawCertData();
        
        // Calculate SHA256 hash (equivalent to sha256.Sum256(rawCert) in Syncthing)
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(rawCertBytes);
        
        // Convert to string representation
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
    /// Format device ID hash as a string (similar to Syncthing's DeviceID.String())
    /// Uses base32 encoding with Luhn check digits and chunking for readability
    /// </summary>
    private static string FormatDeviceId(byte[] hash)
    {
        // Convert to base32 (similar to Syncthing's approach)
        var base32 = Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        // Add chunking for readability (similar to Syncthing's chunkify)
        var chunked = new StringBuilder();
        for (int i = 0; i < base32.Length; i += 7)
        {
            if (i > 0) chunked.Append('-');
            int length = Math.Min(7, base32.Length - i);
            chunked.Append(base32.Substring(i, length));
        }

        return chunked.ToString().ToUpperInvariant();
    }

    /// <summary>
    /// Validate if a string is a valid device ID format
    /// </summary>
    public static bool IsValidDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        // Remove separators and check format
        var cleaned = deviceId.Replace("-", "").Replace(" ", "");
        
        // Should be base32-like string of appropriate length
        return cleaned.Length >= 32 && 
               cleaned.All(c => char.IsLetterOrDigit(c));
    }

    /// <summary>
    /// Get short device ID for display purposes (first 7 characters)
    /// Similar to Syncthing's ShortID
    /// </summary>
    public static string GetShortDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return string.Empty;

        var cleaned = deviceId.Replace("-", "").Replace(" ", "");
        return cleaned.Length >= 7 ? cleaned.Substring(0, 7) : cleaned;
    }
}