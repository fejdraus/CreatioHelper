using System.Security.Cryptography.X509Certificates;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Информация о сертификате устройства (аналог Syncthing certificate management)
/// </summary>
public class CertificateInfo
{
    /// <summary>
    /// Уникальный отпечаток сертификата (SHA-256 hash)
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Алгоритм подписи сертификата
    /// </summary>
    public CertificateSignatureAlgorithm SignatureAlgorithm { get; set; }

    /// <summary>
    /// Дата создания сертификата
    /// </summary>
    public DateTime IssuedAt { get; set; }

    /// <summary>
    /// Дата истечения сертификата
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Subject (Common Name) сертификата
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Issuer сертификата
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Размер ключа в битах
    /// </summary>
    public int KeySize { get; set; }

    /// <summary>
    /// Является ли сертификат самоподписанным
    /// </summary>
    public bool IsSelfSigned { get; set; }

    /// <summary>
    /// Доверенные домены и IP адреса
    /// </summary>
    public List<string> TrustedNames { get; set; } = new();

    /// <summary>
    /// Расширенное использование ключа
    /// </summary>
    public List<string> ExtendedKeyUsage { get; set; } = new();

    /// <summary>
    /// Создать CertificateInfo из X509Certificate2
    /// </summary>
    public static CertificateInfo FromX509Certificate(X509Certificate2 certificate)
    {
        var info = new CertificateInfo
        {
            Fingerprint = certificate.Thumbprint.Replace(":", "").ToLower(),
            IssuedAt = certificate.NotBefore,
            ExpiresAt = certificate.NotAfter,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            KeySize = certificate.GetRSAPublicKey()?.KeySize ?? certificate.GetECDsaPublicKey()?.KeySize ?? 0,
            IsSelfSigned = certificate.Subject == certificate.Issuer
        };

        // Определяем алгоритм подписи
        info.SignatureAlgorithm = certificate.SignatureAlgorithm.FriendlyName?.ToLower() switch
        {
            var x when x?.Contains("rsa") == true => CertificateSignatureAlgorithm.RSA,
            var x when x?.Contains("ecdsa") == true => CertificateSignatureAlgorithm.ECDSA,
            var x when x?.Contains("ed25519") == true => CertificateSignatureAlgorithm.Ed25519,
            _ => CertificateSignatureAlgorithm.Unknown
        };

        // Извлекаем доверенные имена из Subject Alternative Names
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension sanExtension)
            {
                var names = sanExtension.Format(false);
                foreach (var line in names.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmedLine = line.Trim();
                    if (trimmedLine.StartsWith("DNS Name="))
                    {
                        info.TrustedNames.Add(trimmedLine["DNS Name=".Length..]);
                    }
                    else if (trimmedLine.StartsWith("IP Address="))
                    {
                        info.TrustedNames.Add(trimmedLine["IP Address=".Length..]);
                    }
                }
            }
            else if (extension is X509EnhancedKeyUsageExtension ekuExtension)
            {
                foreach (var oid in ekuExtension.EnhancedKeyUsages)
                {
                    info.ExtendedKeyUsage.Add(oid.FriendlyName ?? oid.Value ?? "Unknown");
                }
            }
        }

        return info;
    }

    /// <summary>
    /// Проверить валидность сертификата на указанную дату
    /// </summary>
    public bool IsValidAt(DateTime date)
    {
        return date >= IssuedAt && date <= ExpiresAt;
    }

    /// <summary>
    /// Проверить, истекает ли сертификат в ближайшие дни
    /// </summary>
    public bool IsExpiringSoon(int daysThreshold = 30)
    {
        var threshold = DateTime.UtcNow.AddDays(daysThreshold);
        return ExpiresAt <= threshold;
    }

    /// <summary>
    /// Дни до истечения сертификата
    /// </summary>
    public int DaysUntilExpiry()
    {
        return (int)(ExpiresAt - DateTime.UtcNow).TotalDays;
    }
}

/// <summary>
/// Алгоритмы подписи сертификатов (на основе Syncthing tlsutil)
/// </summary>
public enum CertificateSignatureAlgorithm
{
    Unknown = 0,
    RSA = 1,
    ECDSA = 2,
    Ed25519 = 3
}

/// <summary>
/// Конфигурация безопасности устройства
/// </summary>
public class DeviceSecurityConfiguration
{
    /// <summary>
    /// ID устройства (на основе сертификата)
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Информация о сертификате устройства
    /// </summary>
    public CertificateInfo? Certificate { get; set; }

    /// <summary>
    /// Является ли устройство доверенным (аналог Syncthing untrusted devices)
    /// </summary>
    public bool IsTrusted { get; set; } = true;

    /// <summary>
    /// Разрешить автоматическое подключение
    /// </summary>
    public bool AllowAutoConnect { get; set; } = true;

    /// <summary>
    /// Требовать TLS 1.3 для соединений с этим устройством
    /// </summary>
    public bool RequireTls13 { get; set; } = true;

    /// <summary>
    /// Список разрешенных cipher suites
    /// </summary>
    public List<string> AllowedCipherSuites { get; set; } = new();

    /// <summary>
    /// Максимальное время жизни соединения (в секундах)
    /// </summary>
    public int MaxConnectionLifetimeSeconds { get; set; } = 86400; // 24 часа

    /// <summary>
    /// Время создания конфигурации
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Время последнего обновления
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Проверить, требует ли устройство обновления сертификата
    /// </summary>
    public bool RequiresCertificateRenewal(int daysThreshold = 30)
    {
        return Certificate?.IsExpiringSoon(daysThreshold) ?? false;
    }
}

/// <summary>
/// Настройки безопасности для всей системы синхронизации
/// </summary>
public class SecurityConfiguration
{
    /// <summary>
    /// Глобально требовать TLS 1.3
    /// </summary>
    public bool GlobalRequireTls13 { get; set; } = true;

    /// <summary>
    /// Автоматически обновлять сертификаты при истечении срока действия
    /// </summary>
    public bool AutoRenewCertificates { get; set; } = true;

    /// <summary>
    /// Количество дней до истечения для автообновления
    /// </summary>
    public int CertificateRenewalDaysThreshold { get; set; } = 30;

    /// <summary>
    /// Срок действия новых сертификатов в днях
    /// </summary>
    public int CertificateValidityDays { get; set; } = 3650; // 10 лет

    /// <summary>
    /// Предпочитаемый алгоритм для новых сертификатов
    /// </summary>
    public CertificateSignatureAlgorithm PreferredSignatureAlgorithm { get; set; } = CertificateSignatureAlgorithm.Ed25519;

    /// <summary>
    /// Блокировать подключения от недоверенных устройств
    /// </summary>
    public bool BlockUntrustedDevices { get; set; } = true;

    /// <summary>
    /// Логировать все события безопасности
    /// </summary>
    public bool EnableSecurityAuditLog { get; set; } = true;

    /// <summary>
    /// Максимальное количество неудачных попыток подключения перед блокировкой
    /// </summary>
    public int MaxFailedConnections { get; set; } = 5;

    /// <summary>
    /// Время блокировки после превышения лимита неудачных попыток (в секундах)
    /// </summary>
    public int BlockDurationSeconds { get; set; } = 3600; // 1 час

    /// <summary>
    /// Включить rate limiting для подключений
    /// </summary>
    public bool EnableConnectionRateLimit { get; set; } = true;

    /// <summary>
    /// Максимальное количество подключений в секунду
    /// </summary>
    public int MaxConnectionsPerSecond { get; set; } = 10;
}