using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Интерфейс для управления сертификатами (на основе Syncthing tlsutil)
/// </summary>
public interface ICertificateManager
{
    /// <summary>
    /// Создать новый самоподписанный сертификат
    /// </summary>
    Task<X509Certificate2> CreateDeviceCertificateAsync(
        string commonName,
        int validityDays,
        CertificateSignatureAlgorithm signatureAlgorithm = CertificateSignatureAlgorithm.Ed25519,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Загрузить сертификат из файла
    /// </summary>
    Task<X509Certificate2> LoadCertificateAsync(
        string certificatePath,
        string? privateKeyPath = null,
        string? password = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохранить сертификат в файлы
    /// </summary>
    Task SaveCertificateAsync(
        X509Certificate2 certificate,
        string certificatePath,
        string privateKeyPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить информацию о сертификате
    /// </summary>
    CertificateInfo GetCertificateInfo(X509Certificate2 certificate);

    /// <summary>
    /// Проверить валидность сертификата
    /// </summary>
    Task<CertificateValidationResult> ValidateCertificateAsync(
        X509Certificate2 certificate,
        string? expectedFingerprint = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Вычислить DeviceID из сертификата (SHA-256 hash)
    /// </summary>
    string ComputeDeviceId(X509Certificate2 certificate);

    /// <summary>
    /// Проверить, требует ли сертификат обновления
    /// </summary>
    bool RequiresRenewal(X509Certificate2 certificate, int daysThreshold = 30);

    /// <summary>
    /// Автоматически обновить сертификат если требуется
    /// </summary>
    Task<X509Certificate2?> RenewCertificateIfNeededAsync(
        X509Certificate2 currentCertificate,
        string commonName,
        int validityDays,
        int renewalThreshold = 30,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить конфигурацию TLS на основе требований безопасности
    /// </summary>
    Task<TlsConfiguration> GetTlsConfigurationAsync(
        SecurityConfiguration securityConfig,
        X509Certificate2 deviceCertificate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверить доверенность устройства по сертификату
    /// </summary>
    Task<bool> IsTrustedDeviceAsync(
        X509Certificate2 deviceCertificate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Добавить устройство в список доверенных
    /// </summary>
    Task AddTrustedDeviceAsync(
        string deviceId,
        X509Certificate2 deviceCertificate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Удалить устройство из списка доверенных
    /// </summary>
    Task RemoveTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить список всех доверенных устройств
    /// </summary>
    Task<List<DeviceSecurityConfiguration>> GetTrustedDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Экспортировать сертификат в различные форматы
    /// </summary>
    Task<byte[]> ExportCertificateAsync(
        X509Certificate2 certificate,
        CertificateExportFormat format,
        string? password = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Импортировать сертификат из различных форматов
    /// </summary>
    Task<X509Certificate2> ImportCertificateAsync(
        byte[] certificateData,
        CertificateExportFormat format,
        string? password = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Интерфейс для аудита безопасности
/// </summary>
public interface ISecurityAuditor
{
    /// <summary>
    /// Логировать событие безопасности
    /// </summary>
    Task LogSecurityEventAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить журнал событий безопасности
    /// </summary>
    Task<List<SecurityEvent>> GetSecurityEventsAsync(
        DateTime? since = null,
        SecurityEventType? eventType = null,
        string? deviceId = null,
        int limit = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Провести аудит безопасности системы
    /// </summary>
    Task<SecurityAuditResult> PerformSecurityAuditAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить статистику безопасности
    /// </summary>
    Task<SecurityStatistics> GetSecurityStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Очистить старые события безопасности
    /// </summary>
    Task CleanupOldEventsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

/// <summary>
/// Результат проверки сертификата
/// </summary>
public class CertificateValidationResult
{
    /// <summary>
    /// Успешна ли проверка
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Список ошибок валидации
    /// </summary>
    public List<string> ValidationErrors { get; set; } = new();

    /// <summary>
    /// Список предупреждений
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Детали проверки
    /// </summary>
    public Dictionary<string, object> ValidationDetails { get; set; } = new();

    /// <summary>
    /// Время проверки
    /// </summary>
    public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Конфигурация TLS соединения
/// </summary>
public class TlsConfiguration
{
    /// <summary>
    /// Минимальная версия TLS
    /// </summary>
    public string MinimumTlsVersion { get; set; } = "TLS13";

    /// <summary>
    /// Разрешенные cipher suites
    /// </summary>
    public List<string> AllowedCipherSuites { get; set; } = new();

    /// <summary>
    /// Требовать взаимную аутентификацию
    /// </summary>
    public bool RequireMutualAuthentication { get; set; } = true;

    /// <summary>
    /// Проверять сертификат клиента
    /// </summary>
    public bool ValidateClientCertificate { get; set; } = true;

    /// <summary>
    /// Таймаут рукопожатия в секундах
    /// </summary>
    public int HandshakeTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Использовать сжатие
    /// </summary>
    public bool EnableCompression { get; set; } = false;

    /// <summary>
    /// Размер буфера
    /// </summary>
    public int BufferSize { get; set; } = 16384;
}

/// <summary>
/// События безопасности
/// </summary>
public class SecurityEvent
{
    /// <summary>
    /// Уникальный ID события
    /// </summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Тип события
    /// </summary>
    public SecurityEventType EventType { get; set; }

    /// <summary>
    /// Время события
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// ID устройства
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// IP адрес
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// Описание события
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Уровень серьезности
    /// </summary>
    public SecuritySeverity Severity { get; set; }

    /// <summary>
    /// Дополнительные данные
    /// </summary>
    public Dictionary<string, object> Details { get; set; } = new();
}

/// <summary>
/// Типы событий безопасности
/// </summary>
public enum SecurityEventType
{
    CertificateCreated,
    CertificateRenewed,
    CertificateExpired,
    CertificateRevoked,
    DeviceConnected,
    DeviceDisconnected,
    AuthenticationFailed,
    TlsHandshakeError,
    UntrustedDevice,
    SecurityAuditPerformed,
    ConfigurationChanged,
    SuspiciousActivity
}

/// <summary>
/// Уровни серьезности событий безопасности
/// </summary>
public enum SecuritySeverity
{
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Результат аудита безопасности
/// </summary>
public class SecurityAuditResult
{
    /// <summary>
    /// Общий балл безопасности (0-100)
    /// </summary>
    public int SecurityScore { get; set; }

    /// <summary>
    /// Критические проблемы
    /// </summary>
    public List<SecurityIssue> CriticalIssues { get; set; } = new();

    /// <summary>
    /// Предупреждения
    /// </summary>
    public List<SecurityIssue> Warnings { get; set; } = new();

    /// <summary>
    /// Рекомендации по улучшению
    /// </summary>
    public List<SecurityRecommendation> Recommendations { get; set; } = new();

    /// <summary>
    /// Время проведения аудита
    /// </summary>
    public DateTime AuditTimestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Детали аудита
    /// </summary>
    public Dictionary<string, object> AuditDetails { get; set; } = new();
}

/// <summary>
/// Проблема безопасности
/// </summary>
public class SecurityIssue
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SecuritySeverity Severity { get; set; }
    public string? AffectedComponent { get; set; }
    public string? RecommendedAction { get; set; }
}

/// <summary>
/// Рекомендация по безопасности
/// </summary>
public class SecurityRecommendation
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Priority { get; set; } // 1-10, где 10 самый высокий
    public string? ImplementationGuide { get; set; }
}

/// <summary>
/// Статистика безопасности
/// </summary>
public class SecurityStatistics
{
    /// <summary>
    /// Общее количество событий безопасности
    /// </summary>
    public long TotalSecurityEvents { get; set; }

    /// <summary>
    /// События за последние 24 часа
    /// </summary>
    public long EventsLast24Hours { get; set; }

    /// <summary>
    /// Количество неудачных попыток подключения
    /// </summary>
    public long FailedConnections { get; set; }

    /// <summary>
    /// Количество заблокированных IP
    /// </summary>
    public long BlockedIpAddresses { get; set; }

    /// <summary>
    /// Количество активных доверенных устройств
    /// </summary>
    public long TrustedDevices { get; set; }

    /// <summary>
    /// Количество сертификатов, требующих обновления
    /// </summary>
    public long CertificatesRequiringRenewal { get; set; }

    /// <summary>
    /// Последний аудит безопасности
    /// </summary>
    public DateTime? LastSecurityAudit { get; set; }

    /// <summary>
    /// События по типам
    /// </summary>
    public Dictionary<SecurityEventType, long> EventsByType { get; set; } = new();

    /// <summary>
    /// События по серьезности
    /// </summary>
    public Dictionary<SecuritySeverity, long> EventsBySeverity { get; set; } = new();
}

/// <summary>
/// Форматы экспорта сертификатов
/// </summary>
public enum CertificateExportFormat
{
    Pem,
    Der,
    Pkcs12,
    Pkcs7
}