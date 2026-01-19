using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using System.Net;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Syncthing-compatible TLS configuration manager
/// Based on syncthing/lib/tlsutil/tlsutil.go
/// </summary>
public class SyncthingTlsManager
{
    private readonly ILogger<SyncthingTlsManager> _logger;

    /// <summary>
    /// Cipher suites for TLS 1.2 connections, in Syncthing's exact order
    /// From lib/tlsutil/tlsutil.go cipherSuites
    /// </summary>
    public static readonly TlsCipherSuite[] SyncthingCipherSuites = new[]
    {
        // Good and fast on hardware WITHOUT AES-NI
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,

        // Good and fast on hardware WITH AES-NI
        // 256-bit ciphers first - "because that looks cooler" - Syncthing comment
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,

        // The rest of the suites, minus DES stuff
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA,
        TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA256,
        TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
        TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
    };

    public SyncthingTlsManager(ILogger<SyncthingTlsManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Create TLS 1.3 only configuration - equivalent to Syncthing's SecureDefaultTLS13()
    /// </summary>
    public SslServerAuthenticationOptions CreateTls13OnlyConfig(X509Certificate2 serverCertificate)
    {
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCertificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = ValidateClientCertificate,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption
        };

        _logger.LogDebug("Created TLS 1.3 only configuration");
        return options;
    }

    /// <summary>
    /// Create TLS 1.2 + 1.3 configuration - equivalent to Syncthing's SecureDefaultWithTLS12()
    /// </summary>
    public SslServerAuthenticationOptions CreateTls12And13Config(X509Certificate2 serverCertificate)
    {
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCertificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = ValidateClientCertificate,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption
        };

        // Note: .NET doesn't allow explicit cipher suite configuration like Go
        // The cipher suites are handled by the OS/framework automatically
        _logger.LogDebug("Created TLS 1.2/1.3 configuration with Syncthing-compatible settings");
        return options;
    }

    /// <summary>
    /// Create client authentication options for connecting to other Syncthing devices
    /// </summary>
    public SslClientAuthenticationOptions CreateClientAuthOptions(
        X509Certificate2 clientCertificate, 
        string targetHost,
        bool requireTls13Only = false)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ClientCertificates = new X509CertificateCollection { clientCertificate },
            EnabledSslProtocols = requireTls13Only ? SslProtocols.Tls13 : (SslProtocols.Tls12 | SslProtocols.Tls13),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = ValidateServerCertificate,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption
        };

        _logger.LogDebug("Created client auth options for {TargetHost}, TLS13 only: {Tls13Only}", 
            targetHost, requireTls13Only);
        return options;
    }

    /// <summary>
    /// Validate client certificate - equivalent to Syncthing device ID verification
    /// </summary>
    private bool ValidateClientCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (certificate == null)
        {
            _logger.LogWarning("Client certificate is null");
            return false;
        }

        try
        {
            var cert = new X509Certificate2(certificate);
            var deviceId = ComputeSyncthingDeviceId(cert);
            
            _logger.LogDebug("Validating client certificate with device ID {DeviceId}", deviceId);

            // In Syncthing, device IDs are validated against trusted devices list
            // Валидация сертификата выполняется в CertificateManager.ValidateCertificateAsync()
            // Здесь проверяем только формат self-signed сертификата
            
            // Check if certificate is self-signed (typical for Syncthing)
            if (cert.Subject != cert.Issuer)
            {
                _logger.LogWarning("Client certificate is not self-signed: Subject={Subject}, Issuer={Issuer}", 
                    cert.Subject, cert.Issuer);
            }

            // Check certificate validity
            var now = DateTime.UtcNow;
            if (cert.NotBefore > now || cert.NotAfter < now)
            {
                _logger.LogWarning("Client certificate is not valid at current time: NotBefore={NotBefore}, NotAfter={NotAfter}", 
                    cert.NotBefore, cert.NotAfter);
                return false;
            }

            // Check key usage
            var hasClientAuth = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(ext => ext.EnhancedKeyUsages.Cast<Oid>()
                .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2")); // Client Authentication

            if (!hasClientAuth)
            {
                _logger.LogWarning("Client certificate does not have Client Authentication usage");
                return false;
            }

            _logger.LogInformation("Client certificate validated successfully for device {DeviceId}", deviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating client certificate");
            return false;
        }
    }

    /// <summary>
    /// Validate server certificate when connecting as client
    /// </summary>
    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (certificate == null)
        {
            _logger.LogWarning("Server certificate is null");
            return false;
        }

        try
        {
            var cert = new X509Certificate2(certificate);
            var deviceId = ComputeSyncthingDeviceId(cert);
            
            _logger.LogDebug("Validating server certificate with device ID {DeviceId}", deviceId);

            // Similar validation as client certificate
            var now = DateTime.UtcNow;
            if (cert.NotBefore > now || cert.NotAfter < now)
            {
                _logger.LogWarning("Server certificate is not valid at current time");
                return false;
            }

            // Check server authentication usage
            var hasServerAuth = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(ext => ext.EnhancedKeyUsages.Cast<Oid>()
                .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1")); // Server Authentication

            if (!hasServerAuth)
            {
                _logger.LogWarning("Server certificate does not have Server Authentication usage");
                return false;
            }

            _logger.LogInformation("Server certificate validated successfully for device {DeviceId}", deviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating server certificate");
            return false;
        }
    }

    /// <summary>
    /// Generate Syncthing-compatible certificate for sync connections (Ed25519-like behavior)
    /// equivalent to generateCertificate(commonName, lifetimeDays, compatible=false)
    /// </summary>
    public X509Certificate2 GenerateSyncCertificate(string commonName, int lifetimeDays = 365 * 20)
    {
        // .NET doesn't support Ed25519 directly, so we use ECDSA P-256 as the best alternative
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        
        var notBefore = DateTime.UtcNow.Date; // Truncate to start of day like Syncthing
        var notAfter = notBefore.AddDays(lifetimeDays);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}, O=Syncthing, OU=Automatically Generated"),
            ecdsa,
            HashAlgorithmName.SHA256);

        // Add extensions matching Syncthing template
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
                false));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
                    new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
                }, false));

        // Add DNS name like Syncthing
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(commonName);
        request.CertificateExtensions.Add(sanBuilder.Build());

        // Generate random serial number like Syncthing (rand.Uint64())
        var serialNumber = new byte[8];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(serialNumber);
        }

        var certificate = request.CreateSelfSigned(notBefore, notAfter);
        
        _logger.LogInformation("Generated Syncthing-compatible sync certificate for {CommonName} (expires {ExpiryDate})", 
            commonName, notAfter);

        return certificate;
    }

    /// <summary>
    /// Generate browser-compatible certificate (ECDSA P-256)
    /// equivalent to generateCertificate(commonName, lifetimeDays, compatible=true)
    /// </summary>
    public X509Certificate2 GenerateBrowserCertificate(string commonName, int lifetimeDays = 365)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        
        var notBefore = DateTime.UtcNow.Date;
        var notAfter = notBefore.AddDays(lifetimeDays);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}, O=Syncthing, OU=Automatically Generated"),
            ecdsa,
            HashAlgorithmName.SHA256);

        // Same extensions as sync certificate
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
                false));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
                    new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
                }, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(commonName);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var serialNumber = new byte[8];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(serialNumber);
        }

        var certificate = request.CreateSelfSigned(notBefore, notAfter);
        
        _logger.LogInformation("Generated browser-compatible certificate for {CommonName} (expires {ExpiryDate})", 
            commonName, notAfter);

        return certificate;
    }

    /// <summary>
    /// Compute Syncthing device ID from certificate
    /// Device ID is SHA-256 hash of the certificate's raw data
    /// </summary>
    public string ComputeSyncthingDeviceId(X509Certificate2 certificate)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(certificate.RawData);
        
        // Format as uppercase hex with dashes every 8 characters (Syncthing format)
        var hex = Convert.ToHexString(hash);
        var formatted = string.Join("-", 
            Enumerable.Range(0, hex.Length / 8)
                      .Select(i => hex.Substring(i * 8, Math.Min(8, hex.Length - i * 8))));
        
        return formatted;
    }

    /// <summary>
    /// Save certificate in PEM format like Syncthing
    /// </summary>
    public async Task SaveCertificatePemAsync(X509Certificate2 certificate, string certPath, string keyPath)
    {
        try
        {
            // Create directories if needed
            Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

            // Export certificate to PEM
            var certPem = certificate.ExportCertificatePem();
            await File.WriteAllTextAsync(certPath, certPem);

            // Export private key to PEM  
            var keyPem = certificate.GetECDsaPrivateKey()?.ExportPkcs8PrivateKeyPem() ??
                        certificate.GetRSAPrivateKey()?.ExportPkcs8PrivateKeyPem() ??
                        throw new InvalidOperationException("Cannot export private key");

            await File.WriteAllTextAsync(keyPath, keyPem);

            // Set secure permissions on private key (Unix systems)
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _logger.LogInformation("Saved certificate to {CertPath} and private key to {KeyPath}", certPath, keyPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving certificate to PEM files");
            throw;
        }
    }

    /// <summary>
    /// Load certificate from PEM files like Syncthing
    /// </summary>
    public async Task<X509Certificate2> LoadCertificatePemAsync(string certPath, string keyPath)
    {
        try
        {
            if (!File.Exists(certPath))
                throw new FileNotFoundException($"Certificate file not found: {certPath}");
            if (!File.Exists(keyPath))
                throw new FileNotFoundException($"Private key file not found: {keyPath}");

            var certPem = await File.ReadAllTextAsync(certPath);
            var keyPem = await File.ReadAllTextAsync(keyPath);

            var certificate = X509Certificate2.CreateFromPem(certPem, keyPem);
            
            _logger.LogInformation("Loaded certificate from {CertPath} with device ID {DeviceId}", 
                certPath, ComputeSyncthingDeviceId(certificate));
            
            return certificate;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading certificate from PEM files");
            throw;
        }
    }

    /// <summary>
    /// Check if certificate needs renewal based on Syncthing logic
    /// </summary>
    public bool ShouldRegenerateCertificate(X509Certificate2 certificate)
    {
        // Based on lib/api.shouldRegenerateCertificate() logic
        var now = DateTime.UtcNow;
        
        // Check if expired
        if (certificate.NotAfter <= now)
        {
            _logger.LogWarning("Certificate has expired: {ExpiryDate}", certificate.NotAfter);
            return true;
        }

        // Check if expires within 30 days
        if (certificate.NotAfter <= now.AddDays(30))
        {
            _logger.LogWarning("Certificate expires within 30 days: {ExpiryDate}", certificate.NotAfter);
            return true;
        }

        // Check for key size and algorithm requirements
        var keySize = certificate.GetRSAPublicKey()?.KeySize ?? 
                     certificate.GetECDsaPublicKey()?.KeySize ?? 0;

        if (keySize < 2048)
        {
            _logger.LogWarning("Certificate key size is too small: {KeySize} bits", keySize);
            return true;
        }

        return false;
    }
}