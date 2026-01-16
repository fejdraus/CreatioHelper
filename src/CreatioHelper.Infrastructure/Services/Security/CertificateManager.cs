using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Менеджер сертификатов (на основе Syncthing tlsutil)
/// </summary>
public class CertificateManager : ICertificateManager
{
    private readonly ILogger<CertificateManager> _logger;
    private readonly ISyncDatabase _database;
    private readonly SecurityConfiguration _securityConfig;

    public CertificateManager(
        ILogger<CertificateManager> logger,
        ISyncDatabase database,
        SecurityConfiguration securityConfig)
    {
        _logger = logger;
        _database = database;
        _securityConfig = securityConfig;
    }

    public Task<X509Certificate2> CreateDeviceCertificateAsync(
        string commonName,
        int validityDays,
        CertificateSignatureAlgorithm signatureAlgorithm = CertificateSignatureAlgorithm.Ed25519,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Creating new device certificate with algorithm {Algorithm} for {CommonName}", 
                signatureAlgorithm, commonName);

            AsymmetricAlgorithm privateKey;
            X509SignatureGenerator signatureGenerator;

            // Создаем ключи в зависимости от алгоритма (аналог Syncthing generateCertificate)
            switch (signatureAlgorithm)
            {
                case CertificateSignatureAlgorithm.RSA:
                    var rsaKey = RSA.Create(2048);
                    privateKey = rsaKey;
                    signatureGenerator = X509SignatureGenerator.CreateForRSA(rsaKey, RSASignaturePadding.Pkcs1);
                    break;

                case CertificateSignatureAlgorithm.ECDSA:
                    var ecdsaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    privateKey = ecdsaKey;
                    signatureGenerator = X509SignatureGenerator.CreateForECDsa(ecdsaKey);
                    break;

                case CertificateSignatureAlgorithm.Ed25519:
                    // .NET не поддерживает Ed25519 напрямую, используем ECDSA как fallback
                    _logger.LogWarning("Ed25519 not directly supported in .NET, using ECDSA P-256 instead");
                    goto case CertificateSignatureAlgorithm.ECDSA;

                default:
                    throw new NotSupportedException($"Signature algorithm {signatureAlgorithm} not supported");
            }

            var notBefore = DateTime.UtcNow.Date; // Truncate to start of day like Syncthing
            var notAfter = notBefore.AddDays(validityDays);

            // Создаем запрос на сертификат (аналог Syncthing template)
            CertificateRequest certificateRequest;
            
            if (privateKey is RSA rsa)
            {
                certificateRequest = new CertificateRequest(
                    new X500DistinguishedName($"CN={commonName}, O=CreatioHelper, OU=Automatically Generated"),
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
            }
            else if (privateKey is ECDsa ecdsa)
            {
                certificateRequest = new CertificateRequest(
                    new X500DistinguishedName($"CN={commonName}, O=CreatioHelper, OU=Automatically Generated"),
                    ecdsa,
                    HashAlgorithmName.SHA256);
            }
            else
            {
                throw new NotSupportedException($"Key type {privateKey.GetType().Name} not supported");
            }

            // Добавляем расширения (аналог Syncthing)
            certificateRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));

            certificateRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    false));

            certificateRequest.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection
                    {
                        new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
                        new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
                    }, false));

            // Добавляем Subject Alternative Names
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(commonName);
            certificateRequest.CertificateExtensions.Add(sanBuilder.Build());

            // Генерируем случайный серийный номер (как в Syncthing rand.Uint64())
            var serialNumber = new byte[8];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(serialNumber);
            }
            
            // Генерируем самоподписанный сертификат используя наш signatureGenerator (как в Syncthing)
            var certificate = certificateRequest.Create(
                certificateRequest.SubjectName, // Self-signed: issuer = subject
                signatureGenerator,
                notBefore,
                notAfter,
                serialNumber); // Random serial number как в Syncthing
            
            // Создаем сертификат с приватным ключом (тип-специфичный)
            X509Certificate2 certificateWithKey;
            if (privateKey is RSA rsaPrivateKey)
            {
                certificateWithKey = certificate.CopyWithPrivateKey(rsaPrivateKey);
            }
            else if (privateKey is ECDsa ecdsaPrivateKey)
            {
                certificateWithKey = certificate.CopyWithPrivateKey(ecdsaPrivateKey);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported private key type: {privateKey.GetType()}");
            }

            _logger.LogInformation("Successfully created certificate with fingerprint {Fingerprint}",
                certificateWithKey.Thumbprint);

            return Task.FromResult(certificateWithKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create device certificate");
            throw;
        }
    }

    public async Task<X509Certificate2> LoadCertificateAsync(
        string certificatePath,
        string? privateKeyPath = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading certificate from {Path}", certificatePath);

            if (!File.Exists(certificatePath))
            {
                throw new FileNotFoundException($"Certificate file not found: {certificatePath}");
            }

            X509Certificate2 certificate;

            if (string.IsNullOrEmpty(privateKeyPath))
            {
                // Загружаем сертификат с приватным ключом из одного файла
                certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            else
            {
                // Загружаем сертификат и приватный ключ из отдельных файлов
                if (!File.Exists(privateKeyPath))
                {
                    throw new FileNotFoundException($"Private key file not found: {privateKeyPath}");
                }

                var certBytes = await File.ReadAllBytesAsync(certificatePath, cancellationToken);
                
                // Пытаемся прочитать приватный ключ с обработкой ошибок доступа
                byte[] keyBytes;
                try
                {
                    keyBytes = await File.ReadAllBytesAsync(privateKeyPath, cancellationToken);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Access denied reading private key {Path}, may have restrictive permissions", privateKeyPath);
                    throw new InvalidOperationException($"Cannot read private key file due to access restrictions: {privateKeyPath}");
                }

                certificate = X509Certificate2.CreateFromPem(
                    Encoding.UTF8.GetString(certBytes),
                    Encoding.UTF8.GetString(keyBytes));
            }

            _logger.LogInformation("Successfully loaded certificate with subject {Subject}",
                certificate.Subject);

            return certificate;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load certificate from {Path}", certificatePath);
            throw;
        }
    }

    public async Task SaveCertificateAsync(
        X509Certificate2 certificate,
        string certificatePath,
        string privateKeyPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Saving certificate to {CertPath} and private key to {KeyPath}",
                certificatePath, privateKeyPath);

            // Создаем директории если не существуют (только для абсолютных путей)
            var certDir = Path.GetDirectoryName(Path.GetFullPath(certificatePath));
            var keyDir = Path.GetDirectoryName(Path.GetFullPath(privateKeyPath));
            
            if (!string.IsNullOrEmpty(certDir))
                Directory.CreateDirectory(certDir);
            if (!string.IsNullOrEmpty(keyDir))
                Directory.CreateDirectory(keyDir);

            // Экспортируем сертификат в PEM формате
            var certPem = certificate.ExportCertificatePem();
            await File.WriteAllTextAsync(certificatePath, certPem, cancellationToken);

            // Экспортируем приватный ключ в PEM формате
            var keyPem = certificate.GetRSAPrivateKey()?.ExportPkcs8PrivateKeyPem() ??
                        certificate.GetECDsaPrivateKey()?.ExportPkcs8PrivateKeyPem() ??
                        throw new InvalidOperationException("Cannot export private key");

            await File.WriteAllTextAsync(privateKeyPath, keyPem, cancellationToken);

            // Устанавливаем безопасные права доступа к приватному ключу
            if (OperatingSystem.IsWindows())
            {
                // На Windows просто пропускаем установку специальных прав доступа для тестирования
                _logger.LogDebug("Skipping Windows file permissions setup for testing environment");
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // На Unix системах устанавливаем режим 600 (только владелец может читать/писать)
                File.SetUnixFileMode(privateKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _logger.LogInformation("Successfully saved certificate and private key");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save certificate");
            throw;
        }
    }

    public CertificateInfo GetCertificateInfo(X509Certificate2 certificate)
    {
        return CertificateInfo.FromX509Certificate(certificate);
    }

    public Task<CertificateValidationResult> ValidateCertificateAsync(
        X509Certificate2 certificate,
        string? expectedFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        var result = new CertificateValidationResult();

        try
        {
            // Проверяем срок действия
            var now = DateTime.UtcNow;
            if (certificate.NotBefore > now)
            {
                result.ValidationErrors.Add("Certificate is not yet valid");
            }
            if (certificate.NotAfter < now)
            {
                result.ValidationErrors.Add("Certificate has expired");
            }
            else if (certificate.NotAfter < now.AddDays(30))
            {
                result.Warnings.Add("Certificate will expire within 30 days");
            }

            // Проверяем отпечаток если указан
            if (!string.IsNullOrEmpty(expectedFingerprint))
            {
                var actualFingerprint = certificate.Thumbprint.Replace(":", "").ToLower();
                if (!actualFingerprint.Equals(expectedFingerprint.Replace(":", "").ToLower()))
                {
                    result.ValidationErrors.Add("Certificate fingerprint does not match expected value");
                }
            }

            // Проверяем алгоритм подписи
            var certInfo = GetCertificateInfo(certificate);
            if (certInfo.SignatureAlgorithm == CertificateSignatureAlgorithm.Unknown)
            {
                result.Warnings.Add("Unknown or unsupported signature algorithm");
            }

            // Проверяем размер ключа
            if (certInfo.KeySize < 2048)
            {
                result.ValidationErrors.Add("Key size is too small (minimum 2048 bits)");
            }

            // Проверяем использование ключа
            var hasServerAuth = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(ext => ext.EnhancedKeyUsages.Cast<Oid>()
                .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1")); // Server Authentication

            var hasClientAuth = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(ext => ext.EnhancedKeyUsages.Cast<Oid>()
                .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2")); // Client Authentication

            if (!hasServerAuth)
            {
                result.ValidationErrors.Add("Certificate does not have Server Authentication usage");
            }
            if (!hasClientAuth)
            {
                result.ValidationErrors.Add("Certificate does not have Client Authentication usage");
            }

            result.ValidationDetails["Fingerprint"] = certificate.Thumbprint;
            result.ValidationDetails["Subject"] = certificate.Subject;
            result.ValidationDetails["Issuer"] = certificate.Issuer;
            result.ValidationDetails["ValidFrom"] = certificate.NotBefore;
            result.ValidationDetails["ValidTo"] = certificate.NotAfter;
            result.ValidationDetails["KeySize"] = certInfo.KeySize;
            result.ValidationDetails["SignatureAlgorithm"] = certInfo.SignatureAlgorithm.ToString();

            result.IsValid = result.ValidationErrors.Count == 0;

            if (result.IsValid)
            {
                _logger.LogInformation("Certificate validation successful for {Subject}", certificate.Subject);
            }
            else
            {
                _logger.LogWarning("Certificate validation failed for {Subject}: {Errors}",
                    certificate.Subject, string.Join(", ", result.ValidationErrors));
            }
        }
        catch (Exception ex)
        {
            result.ValidationErrors.Add($"Validation error: {ex.Message}");
            result.IsValid = false;
            _logger.LogError(ex, "Error validating certificate");
        }

        return Task.FromResult(result);
    }

    public string ComputeDeviceId(X509Certificate2 certificate)
    {
        // Создаем DeviceID как SHA-256 hash от raw certificate data (аналог Syncthing)
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(certificate.RawData);
        return Convert.ToHexString(hash).ToLower();
    }

    public bool RequiresRenewal(X509Certificate2 certificate, int daysThreshold = 30)
    {
        var threshold = DateTime.UtcNow.AddDays(daysThreshold);
        return certificate.NotAfter <= threshold;
    }

    public async Task<X509Certificate2?> RenewCertificateIfNeededAsync(
        X509Certificate2 currentCertificate,
        string commonName,
        int validityDays,
        int renewalThreshold = 30,
        CancellationToken cancellationToken = default)
    {
        if (!RequiresRenewal(currentCertificate, renewalThreshold))
        {
            return null;
        }

        _logger.LogInformation("Renewing certificate for {CommonName} (expires on {ExpiryDate})",
            commonName, currentCertificate.NotAfter);

        var newCertificate = await CreateDeviceCertificateAsync(
            commonName,
            validityDays,
            _securityConfig.PreferredSignatureAlgorithm,
            cancellationToken);

        _logger.LogInformation("Successfully renewed certificate with new fingerprint {Fingerprint}",
            newCertificate.Thumbprint);

        return newCertificate;
    }

    public Task<TlsConfiguration> GetTlsConfigurationAsync(
        SecurityConfiguration securityConfig,
        X509Certificate2 deviceCertificate,
        CancellationToken cancellationToken = default)
    {
        var config = new TlsConfiguration
        {
            MinimumTlsVersion = securityConfig.GlobalRequireTls13 ? "TLS13" : "TLS12",
            RequireMutualAuthentication = true,
            ValidateClientCertificate = true,
            HandshakeTimeoutSeconds = 30,
            EnableCompression = false,
            BufferSize = 16384
        };

        // Добавляем рекомендованные cipher suites (аналог Syncthing cipherSuites)
        if (!securityConfig.GlobalRequireTls13)
        {
            config.AllowedCipherSuites.AddRange(new[]
            {
                "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256",
                "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256",
                "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384",
                "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384",
                "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
                "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256"
            });
        }

        return Task.FromResult(config);
    }

    public async Task<bool> IsTrustedDeviceAsync(
        X509Certificate2 deviceCertificate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var deviceId = ComputeDeviceId(deviceCertificate);
            var trustedDevices = await GetTrustedDevicesAsync(cancellationToken);
            return trustedDevices.Any(d => d.DeviceId == deviceId && d.IsTrusted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if device is trusted");
            return false;
        }
    }

    public Task AddTrustedDeviceAsync(
        string deviceId,
        X509Certificate2 deviceCertificate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var deviceConfig = new DeviceSecurityConfiguration
            {
                DeviceId = deviceId,
                Certificate = GetCertificateInfo(deviceCertificate),
                IsTrusted = true,
                AllowAutoConnect = true,
                RequireTls13 = _securityConfig.GlobalRequireTls13
            };

            // Сохраняем в базе данных
            // Предполагаем что база данных имеет таблицу DeviceSecurityConfigurations
            _logger.LogInformation("Adding trusted device {DeviceId}", deviceId);

            // TODO: Реализовать сохранение в базу данных
            // await _database.SaveDeviceSecurityConfigurationAsync(deviceConfig, cancellationToken);
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding trusted device {DeviceId}", deviceId);
            return Task.FromException(ex);
        }
    }

    public Task RemoveTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Removing trusted device {DeviceId}", deviceId);

            // TODO: Реализовать удаление из базы данных
            // await _database.DeleteDeviceSecurityConfigurationAsync(deviceId, cancellationToken);
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing trusted device {DeviceId}", deviceId);
            return Task.FromException(ex);
        }
    }

    public Task<List<DeviceSecurityConfiguration>> GetTrustedDevicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // TODO: Реализовать загрузку из базы данных
            // return await _database.GetDeviceSecurityConfigurationsAsync(cancellationToken);
            return Task.FromResult(new List<DeviceSecurityConfiguration>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trusted devices");
            return Task.FromResult(new List<DeviceSecurityConfiguration>());
        }
    }

    public Task<byte[]> ExportCertificateAsync(
        X509Certificate2 certificate,
        CertificateExportFormat format,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = format switch
            {
                CertificateExportFormat.Pem => Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()),
                CertificateExportFormat.Der => certificate.Export(X509ContentType.Cert),
                CertificateExportFormat.Pkcs12 => certificate.Export(X509ContentType.Pkcs12, password),
                CertificateExportFormat.Pkcs7 => certificate.Export(X509ContentType.Pkcs7),
                _ => throw new NotSupportedException($"Export format {format} not supported")
            };
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting certificate in format {Format}", format);
            throw;
        }
    }

    public Task<X509Certificate2> ImportCertificateAsync(
        byte[] certificateData,
        CertificateExportFormat format,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = format switch
            {
                CertificateExportFormat.Pem => X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(certificateData)),
                CertificateExportFormat.Der => X509CertificateLoader.LoadCertificate(certificateData),
                CertificateExportFormat.Pkcs12 => X509CertificateLoader.LoadPkcs12(certificateData, password),
                CertificateExportFormat.Pkcs7 => X509CertificateLoader.LoadCertificate(certificateData),
                _ => throw new NotSupportedException($"Import format {format} not supported")
            };
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing certificate in format {Format}", format);
            throw;
        }
    }
}