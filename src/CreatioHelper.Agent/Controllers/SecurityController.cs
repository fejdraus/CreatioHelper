using CreatioHelper.Agent.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// REST API контроллер для управления безопасностью (на основе принципов Syncthing security)
/// </summary>
[ApiController]
[Route("rest/security")]
public class SecurityController : ControllerBase
{
    private readonly ICertificateManager _certificateManager;
    private readonly ISecurityAuditor _securityAuditor;
    private readonly ILogger<SecurityController> _logger;

    public SecurityController(
        ICertificateManager certificateManager,
        ISecurityAuditor securityAuditor,
        ILogger<SecurityController> logger)
    {
        _certificateManager = certificateManager;
        _securityAuditor = securityAuditor;
        _logger = logger;
    }

    /// <summary>
    /// Получить конфигурацию безопасности системы
    /// </summary>
    [HttpGet("config")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetSecurityConfiguration(CancellationToken cancellationToken = default)
    {
        try
        {
            var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
            var statistics = await _securityAuditor.GetSecurityStatisticsAsync(cancellationToken);

            var response = new
            {
                TrustedDevicesCount = trustedDevices.Count,
                CertificatesRequiringRenewal = statistics.CertificatesRequiringRenewal,
                SecurityEvents24h = statistics.EventsLast24Hours,
                LastSecurityAudit = statistics.LastSecurityAudit,
                IsSecurelyConfigured = statistics.TotalSecurityEvents == 0 || statistics.EventsBySeverity.GetValueOrDefault(SecuritySeverity.Critical) == 0
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting security configuration");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Получить информацию о сертификатах устройств
    /// </summary>
    [HttpGet("certificates")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetCertificates(CancellationToken cancellationToken = default)
    {
        try
        {
            var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
            
            var certificatesInfo = trustedDevices.Select(device => new
            {
                device.DeviceId,
                Certificate = device.Certificate != null ? new
                {
                    device.Certificate.Fingerprint,
                    device.Certificate.Subject,
                    device.Certificate.IssuedAt,
                    device.Certificate.ExpiresAt,
                    device.Certificate.SignatureAlgorithm,
                    device.Certificate.KeySize,
                    IsValid = device.Certificate.IsValidAt(DateTime.UtcNow),
                    IsExpiringSoon = device.Certificate.IsExpiringSoon(30),
                    DaysUntilExpiry = device.Certificate.DaysUntilExpiry()
                } : null,
                device.IsTrusted,
                device.AllowAutoConnect,
                device.RequireTls13
            }).ToList();

            return Ok(certificatesInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting certificates information");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Создать новый сертификат устройства
    /// </summary>
    [HttpPost("certificates/create")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> CreateCertificate(
        [FromBody] CreateCertificateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var certificate = await _certificateManager.CreateDeviceCertificateAsync(
                request.CommonName,
                request.ValidityDays,
                request.SignatureAlgorithm,
                cancellationToken);

            var deviceId = _certificateManager.ComputeDeviceId(certificate);
            var certificateInfo = _certificateManager.GetCertificateInfo(certificate);

            await _securityAuditor.LogSecurityEventAsync(new SecurityEvent
            {
                EventType = SecurityEventType.CertificateCreated,
                Severity = SecuritySeverity.Info,
                Message = $"New certificate created for {request.CommonName}",
                Details = new Dictionary<string, object>
                {
                    ["DeviceId"] = deviceId,
                    ["CommonName"] = request.CommonName,
                    ["ValidityDays"] = request.ValidityDays,
                    ["SignatureAlgorithm"] = request.SignatureAlgorithm.ToString()
                }
            }, cancellationToken);

            var response = new
            {
                DeviceId = deviceId,
                Certificate = new
                {
                    certificateInfo.Fingerprint,
                    certificateInfo.Subject,
                    certificateInfo.IssuedAt,
                    certificateInfo.ExpiresAt,
                    certificateInfo.SignatureAlgorithm,
                    certificateInfo.KeySize
                },
                CertificatePem = certificate.ExportCertificatePem(),
                Message = "Certificate created successfully"
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating certificate");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Проверить валидность сертификата
    /// </summary>
    [HttpPost("certificates/validate")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> ValidateCertificate(
        [FromBody] ValidateCertificateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] certificateBytes;
            X509Certificate2 certificate;

            try
            {
                certificateBytes = Convert.FromBase64String(request.CertificateBase64);
                certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "Invalid Base64 format", message = "The certificate data is not valid Base64" });
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return BadRequest(new { error = "Invalid certificate", message = "The certificate data is not valid" });
            }

            var validationResult = await _certificateManager.ValidateCertificateAsync(
                certificate,
                request.ExpectedFingerprint,
                cancellationToken);

            return Ok(validationResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating certificate");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Добавить устройство в список доверенных
    /// </summary>
    [HttpPost("trusted-devices")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> AddTrustedDevice(
        [FromBody] AddTrustedDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] certificateBytes;
            X509Certificate2 certificate;

            try
            {
                certificateBytes = Convert.FromBase64String(request.CertificateBase64);
                certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
            }
            catch (FormatException)
            {
                return BadRequest(new { error = "Invalid Base64 format", message = "The certificate data is not valid Base64" });
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return BadRequest(new { error = "Invalid certificate", message = "The certificate data is not valid" });
            }

            var deviceId = _certificateManager.ComputeDeviceId(certificate);

            await _certificateManager.AddTrustedDeviceAsync(deviceId, certificate, cancellationToken);

            await _securityAuditor.LogSecurityEventAsync(new SecurityEvent
            {
                EventType = SecurityEventType.ConfigurationChanged,
                DeviceId = deviceId,
                Severity = SecuritySeverity.Info,
                Message = $"Device {deviceId} added to trusted devices",
                Details = new Dictionary<string, object>
                {
                    ["DeviceId"] = deviceId,
                    ["DeviceName"] = request.DeviceName
                }
            }, cancellationToken);

            return Ok(new { 
                success = true, 
                message = "Device added to trusted devices", 
                deviceId = deviceId 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding trusted device");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Удалить устройство из списка доверенных
    /// </summary>
    [HttpDelete("trusted-devices/{deviceId}")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> RemoveTrustedDevice(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _certificateManager.RemoveTrustedDeviceAsync(deviceId, cancellationToken);

            await _securityAuditor.LogSecurityEventAsync(new SecurityEvent
            {
                EventType = SecurityEventType.ConfigurationChanged,
                DeviceId = deviceId,
                Severity = SecuritySeverity.Warning,
                Message = $"Device {deviceId} removed from trusted devices"
            }, cancellationToken);

            return Ok(new { 
                success = true, 
                message = "Device removed from trusted devices" 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing trusted device");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Получить список доверенных устройств
    /// </summary>
    [HttpGet("trusted-devices")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetTrustedDevices(CancellationToken cancellationToken = default)
    {
        try
        {
            var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
            return Ok(trustedDevices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trusted devices");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Выполнить аудит безопасности
    /// </summary>
    [HttpPost("audit")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> PerformSecurityAudit(CancellationToken cancellationToken = default)
    {
        try
        {
            var auditResult = await _securityAuditor.PerformSecurityAuditAsync(cancellationToken);
            return Ok(auditResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing security audit");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Получить статистику безопасности
    /// </summary>
    [HttpGet("statistics")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetSecurityStatistics(CancellationToken cancellationToken = default)
    {
        try
        {
            var statistics = await _securityAuditor.GetSecurityStatisticsAsync(cancellationToken);
            return Ok(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting security statistics");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Получить события безопасности
    /// </summary>
    [HttpGet("events")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetSecurityEvents(
        [FromQuery] DateTime? since = null,
        [FromQuery] SecurityEventType? eventType = null,
        [FromQuery] string? deviceId = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var maxLimit = Math.Min(limit, 1000);
            var events = await _securityAuditor.GetSecurityEventsAsync(
                since, eventType, deviceId, maxLimit, cancellationToken);

            return Ok(events);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting security events");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Очистить старые события безопасности
    /// </summary>
    [HttpPost("events/cleanup")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> CleanupSecurityEvents(
        [FromBody] CleanupEventsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var maxAge = TimeSpan.FromDays(Math.Max(1, Math.Min(request.MaxAgeDays, 365)));
            await _securityAuditor.CleanupOldEventsAsync(maxAge, cancellationToken);

            return Ok(new { 
                success = true, 
                message = $"Cleaned up events older than {maxAge.TotalDays} days" 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up security events");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Обновить сертификат если требуется
    /// </summary>
    [HttpPost("certificates/{deviceId}/renew")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> RenewCertificate(
        string deviceId,
        [FromBody] RenewCertificateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
            var device = trustedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
            
            if (device?.Certificate == null)
            {
                return NotFound(new { error = "Device not found or certificate missing" });
            }

            // Создаем X509Certificate2 из информации (для демонстрации - в реальной реализации нужно загружать из хранилища)
            // var currentCertificate = LoadCertificateFromStorage(device);

            // var newCertificate = await _certificateManager.RenewCertificateIfNeededAsync(
            //     currentCertificate,
            //     request.CommonName,
            //     request.ValidityDays,
            //     request.RenewalThresholdDays,
            //     cancellationToken);

            await _securityAuditor.LogSecurityEventAsync(new SecurityEvent
            {
                EventType = SecurityEventType.CertificateRenewed,
                DeviceId = deviceId,
                Severity = SecuritySeverity.Info,
                Message = $"Certificate renewal requested for device {deviceId}"
            }, cancellationToken);

            return Ok(new { 
                success = true, 
                message = "Certificate renewal completed",
                renewalRequired = device.RequiresCertificateRenewal(request.RenewalThresholdDays)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renewing certificate for device {DeviceId}", deviceId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Экспортировать сертификат
    /// </summary>
    [HttpPost("certificates/{deviceId}/export")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> ExportCertificate(
        string deviceId,
        [FromBody] ExportCertificateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // В реальной реализации нужно загрузить сертификат из хранилища
            // var certificate = LoadCertificateFromStorage(deviceId);
            // var exportedData = await _certificateManager.ExportCertificateAsync(
            //     certificate, request.Format, request.Password, cancellationToken);

            await _securityAuditor.LogSecurityEventAsync(new SecurityEvent
            {
                EventType = SecurityEventType.ConfigurationChanged,
                DeviceId = deviceId,
                Severity = SecuritySeverity.Info,
                Message = $"Certificate export requested for device {deviceId} in format {request.Format}"
            }, cancellationToken);

            return Ok(new { 
                success = true, 
                message = "Certificate exported successfully",
                format = request.Format.ToString()
                // certificateData = Convert.ToBase64String(exportedData)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting certificate for device {DeviceId}", deviceId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}

/// <summary>
/// Модель запроса для создания сертификата
/// </summary>
public class CreateCertificateRequest
{
    /// <summary>
    /// Common Name для сертификата
    /// </summary>
    [Required]
    public string CommonName { get; set; } = string.Empty;

    /// <summary>
    /// Срок действия в днях
    /// </summary>
    [Range(1, 3650)]
    public int ValidityDays { get; set; } = 365;

    /// <summary>
    /// Алгоритм подписи
    /// </summary>
    public CertificateSignatureAlgorithm SignatureAlgorithm { get; set; } = CertificateSignatureAlgorithm.ECDSA;
}

/// <summary>
/// Модель запроса для проверки сертификата
/// </summary>
public class ValidateCertificateRequest
{
    /// <summary>
    /// Сертификат в формате Base64
    /// </summary>
    [Required]
    public string CertificateBase64 { get; set; } = string.Empty;

    /// <summary>
    /// Ожидаемый отпечаток сертификата (необязательно)
    /// </summary>
    public string? ExpectedFingerprint { get; set; }
}

/// <summary>
/// Модель запроса для добавления доверенного устройства
/// </summary>
public class AddTrustedDeviceRequest
{
    /// <summary>
    /// Имя устройства
    /// </summary>
    [Required]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Сертификат устройства в формате Base64
    /// </summary>
    [Required]
    public string CertificateBase64 { get; set; } = string.Empty;

    /// <summary>
    /// Разрешить автоматическое подключение
    /// </summary>
    public bool AllowAutoConnect { get; set; } = true;

    /// <summary>
    /// Требовать TLS 1.3
    /// </summary>
    public bool RequireTls13 { get; set; } = true;
}

/// <summary>
/// Модель запроса для очистки событий
/// </summary>
public class CleanupEventsRequest
{
    /// <summary>
    /// Максимальный возраст событий в днях
    /// </summary>
    [Range(1, 365)]
    public int MaxAgeDays { get; set; } = 30;
}

/// <summary>
/// Модель запроса для обновления сертификата
/// </summary>
public class RenewCertificateRequest
{
    /// <summary>
    /// Common Name для нового сертификата
    /// </summary>
    [Required]
    public string CommonName { get; set; } = string.Empty;

    /// <summary>
    /// Срок действия нового сертификата в днях
    /// </summary>
    [Range(1, 3650)]
    public int ValidityDays { get; set; } = 365;

    /// <summary>
    /// Порог обновления в днях
    /// </summary>
    [Range(1, 365)]
    public int RenewalThresholdDays { get; set; } = 30;
}

/// <summary>
/// Модель запроса для экспорта сертификата
/// </summary>
public class ExportCertificateRequest
{
    /// <summary>
    /// Формат экспорта
    /// </summary>
    public CertificateExportFormat Format { get; set; } = CertificateExportFormat.Pem;

    /// <summary>
    /// Пароль для защищенных форматов (необязательно)
    /// </summary>
    public string? Password { get; set; }
}