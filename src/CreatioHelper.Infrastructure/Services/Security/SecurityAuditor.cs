#pragma warning disable CS1998 // Async method lacks await (for placeholder methods)
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Аудитор безопасности (на основе принципов Syncthing security)
/// </summary>
public class SecurityAuditor : BackgroundService, ISecurityAuditor
{
    private readonly ILogger<SecurityAuditor> _logger;
    private readonly ISyncDatabase _database;
    private readonly ICertificateManager _certificateManager;
    private readonly SecurityConfiguration _securityConfig;
    private readonly List<SecurityEvent> _eventBuffer = new();
    private readonly object _bufferLock = new object();
    private readonly Timer _flushTimer;

    public SecurityAuditor(
        ILogger<SecurityAuditor> logger,
        ISyncDatabase database,
        ICertificateManager certificateManager,
        SecurityConfiguration securityConfig)
    {
        _logger = logger;
        _database = database;
        _certificateManager = certificateManager;
        _securityConfig = securityConfig;

        // Настраиваем таймер для периодического сброса событий в БД
        _flushTimer = new Timer(FlushEventBuffer, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task LogSecurityEventAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            // Добавляем событие в буфер для батчевой записи
            lock (_bufferLock)
            {
                _eventBuffer.Add(securityEvent);
            }

            // Логируем критические события немедленно
            if (securityEvent.Severity == SecuritySeverity.Critical)
            {
                _logger.LogCritical("SECURITY CRITICAL: {EventType} - {Message} (Device: {DeviceId}, IP: {IpAddress})",
                    securityEvent.EventType, securityEvent.Message, securityEvent.DeviceId, securityEvent.IpAddress);

                // Немедленно сбрасываем критические события
                await FlushEventBufferAsync(cancellationToken);
            }
            else if (securityEvent.Severity == SecuritySeverity.Error)
            {
                _logger.LogError("SECURITY ERROR: {EventType} - {Message} (Device: {DeviceId})",
                    securityEvent.EventType, securityEvent.Message, securityEvent.DeviceId);
            }
            else if (securityEvent.Severity == SecuritySeverity.Warning)
            {
                _logger.LogWarning("SECURITY WARNING: {EventType} - {Message} (Device: {DeviceId})",
                    securityEvent.EventType, securityEvent.Message, securityEvent.DeviceId);
            }
            else
            {
                _logger.LogInformation("Security event: {EventType} - {Message} (Device: {DeviceId})",
                    securityEvent.EventType, securityEvent.Message, securityEvent.DeviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging security event");
        }
    }

    public async Task<List<SecurityEvent>> GetSecurityEventsAsync(
        DateTime? since = null,
        SecurityEventType? eventType = null,
        string? deviceId = null,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Сначала сбрасываем буфер чтобы получить актуальные данные
            await FlushEventBufferAsync(cancellationToken);

            // TODO: Реализовать загрузку из базы данных с фильтрацией
            // return await _database.GetSecurityEventsAsync(since, eventType, deviceId, limit, cancellationToken);
            
            // Временная реализация - возвращаем события из буфера
            lock (_bufferLock)
            {
                var query = _eventBuffer.AsEnumerable();

                if (since.HasValue)
                    query = query.Where(e => e.Timestamp >= since.Value);

                if (eventType.HasValue)
                    query = query.Where(e => e.EventType == eventType.Value);

                if (!string.IsNullOrEmpty(deviceId))
                    query = query.Where(e => e.DeviceId == deviceId);

                return query.OrderByDescending(e => e.Timestamp)
                           .Take(limit)
                           .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving security events");
            return new List<SecurityEvent>();
        }
    }

    public async Task<SecurityAuditResult> PerformSecurityAuditAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting comprehensive security audit");

            var auditResult = new SecurityAuditResult();
            var score = 100; // Начинаем с максимального балла и вычитаем за проблемы

            // 1. Проверяем конфигурацию безопасности
            score = await AuditSecurityConfigurationAsync(auditResult, score, cancellationToken);

            // 2. Проверяем сертификаты
            score = await AuditCertificatesAsync(auditResult, score, cancellationToken);

            // 3. Проверяем доверенные устройства
            score = await AuditTrustedDevicesAsync(auditResult, score, cancellationToken);

            // 4. Анализируем события безопасности
            score = await AuditSecurityEventsAsync(auditResult, score, cancellationToken);

            // 5. Проверяем сетевую безопасность
            score = await AuditNetworkSecurityAsync(auditResult, score, cancellationToken);

            auditResult.SecurityScore = Math.Max(0, score); // Не может быть отрицательным

            // Генерируем рекомендации на основе найденных проблем
            GenerateRecommendations(auditResult);

            // Логируем событие аудита
            await LogSecurityEventAsync(new SecurityEvent
            {
                EventType = SecurityEventType.SecurityAuditPerformed,
                Severity = SecuritySeverity.Info,
                Message = $"Security audit completed with score {auditResult.SecurityScore}/100",
                Details = new Dictionary<string, object>
                {
                    ["SecurityScore"] = auditResult.SecurityScore,
                    ["CriticalIssues"] = auditResult.CriticalIssues.Count,
                    ["Warnings"] = auditResult.Warnings.Count
                }
            }, cancellationToken);

            _logger.LogInformation("Security audit completed with score {Score}/100. Found {CriticalIssues} critical issues and {Warnings} warnings",
                auditResult.SecurityScore, auditResult.CriticalIssues.Count, auditResult.Warnings.Count);

            return auditResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing security audit");
            
            return new SecurityAuditResult
            {
                SecurityScore = 0,
                CriticalIssues = new List<SecurityIssue>
                {
                    new SecurityIssue
                    {
                        Title = "Audit Failed",
                        Description = $"Security audit failed with error: {ex.Message}",
                        Severity = SecuritySeverity.Critical
                    }
                }
            };
        }
    }

    public async Task<SecurityStatistics> GetSecurityStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await FlushEventBufferAsync(cancellationToken);

            var stats = new SecurityStatistics();
            
            // TODO: Реализовать загрузку статистики из базы данных
            // Временная реализация на основе буфера
            lock (_bufferLock)
            {
                stats.TotalSecurityEvents = _eventBuffer.Count;
                stats.EventsLast24Hours = _eventBuffer.Count(e => e.Timestamp >= DateTime.UtcNow.AddDays(-1));
                
                stats.EventsByType = _eventBuffer
                    .GroupBy(e => e.EventType)
                    .ToDictionary(g => g.Key, g => (long)g.Count());

                stats.EventsBySeverity = _eventBuffer
                    .GroupBy(e => e.Severity)
                    .ToDictionary(g => g.Key, g => (long)g.Count());

                stats.FailedConnections = _eventBuffer.Count(e => e.EventType == SecurityEventType.AuthenticationFailed);
            }

            var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
            stats.TrustedDevices = trustedDevices.Count;
            stats.CertificatesRequiringRenewal = trustedDevices.Count(d => d.RequiresCertificateRenewal());

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting security statistics");
            return new SecurityStatistics();
        }
    }

    public async Task CleanupOldEventsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow - maxAge;
            
            // Очищаем буфер
            lock (_bufferLock)
            {
                var oldEventsCount = _eventBuffer.Count;
                _eventBuffer.RemoveAll(e => e.Timestamp < cutoffDate);
                var removedCount = oldEventsCount - _eventBuffer.Count;
                
                if (removedCount > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} old security events from buffer", removedCount);
                }
            }

            // TODO: Очищаем старые события из базы данных
            // await _database.CleanupOldSecurityEventsAsync(cutoffDate, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old security events");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Security auditor background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Периодически выполняем аудит безопасности
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                
                if (_securityConfig.EnableSecurityAuditLog)
                {
                    await PerformSecurityAuditAsync(stoppingToken);
                }

                // Очищаем старые события
                await CleanupOldEventsAsync(TimeSpan.FromDays(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in security auditor background service");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("Security auditor background service stopped");
    }

    private async Task<int> AuditSecurityConfigurationAsync(SecurityAuditResult auditResult, int score, CancellationToken cancellationToken)
    {
        // Проверяем основные настройки безопасности
        if (!_securityConfig.GlobalRequireTls13)
        {
            auditResult.Warnings.Add(new SecurityIssue
            {
                Title = "TLS 1.3 Not Required",
                Description = "TLS 1.3 is not globally required. Consider enabling it for better security.",
                Severity = SecuritySeverity.Warning,
                AffectedComponent = "SecurityConfiguration",
                RecommendedAction = "Enable GlobalRequireTls13 setting"
            });
            score -= 5;
        }

        if (!_securityConfig.AutoRenewCertificates)
        {
            auditResult.Warnings.Add(new SecurityIssue
            {
                Title = "Auto-Renewal Disabled",
                Description = "Certificate auto-renewal is disabled. Certificates may expire unexpectedly.",
                Severity = SecuritySeverity.Warning,
                AffectedComponent = "SecurityConfiguration",
                RecommendedAction = "Enable AutoRenewCertificates setting"
            });
            score -= 3;
        }

        if (!_securityConfig.BlockUntrustedDevices)
        {
            auditResult.CriticalIssues.Add(new SecurityIssue
            {
                Title = "Untrusted Devices Allowed",
                Description = "Untrusted devices are not blocked, which poses a significant security risk.",
                Severity = SecuritySeverity.Critical,
                AffectedComponent = "SecurityConfiguration",
                RecommendedAction = "Enable BlockUntrustedDevices setting"
            });
            score -= 15;
        }

        return score;
    }

    private async Task<int> AuditCertificatesAsync(SecurityAuditResult auditResult, int score, CancellationToken cancellationToken)
    {
        var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
        
        foreach (var device in trustedDevices)
        {
            if (device.Certificate == null)
            {
                auditResult.Warnings.Add(new SecurityIssue
                {
                    Title = "Missing Certificate Info",
                    Description = $"Device {device.DeviceId} lacks certificate information",
                    Severity = SecuritySeverity.Warning,
                    AffectedComponent = $"Device {device.DeviceId}"
                });
                score -= 2;
                continue;
            }

            // Проверяем истечение срока
            if (!device.Certificate.IsValidAt(DateTime.UtcNow))
            {
                auditResult.CriticalIssues.Add(new SecurityIssue
                {
                    Title = "Expired Certificate",
                    Description = $"Certificate for device {device.DeviceId} has expired",
                    Severity = SecuritySeverity.Critical,
                    AffectedComponent = $"Device {device.DeviceId}",
                    RecommendedAction = "Renew the certificate immediately"
                });
                score -= 20;
            }
            else if (device.Certificate.IsExpiringSoon(30))
            {
                auditResult.Warnings.Add(new SecurityIssue
                {
                    Title = "Certificate Expiring Soon",
                    Description = $"Certificate for device {device.DeviceId} expires in {device.Certificate.DaysUntilExpiry()} days",
                    Severity = SecuritySeverity.Warning,
                    AffectedComponent = $"Device {device.DeviceId}",
                    RecommendedAction = "Schedule certificate renewal"
                });
                score -= 5;
            }

            // Проверяем алгоритм подписи
            if (device.Certificate.SignatureAlgorithm == CertificateSignatureAlgorithm.RSA && device.Certificate.KeySize < 2048)
            {
                auditResult.CriticalIssues.Add(new SecurityIssue
                {
                    Title = "Weak RSA Key",
                    Description = $"Device {device.DeviceId} uses RSA key smaller than 2048 bits",
                    Severity = SecuritySeverity.Critical,
                    AffectedComponent = $"Device {device.DeviceId}",
                    RecommendedAction = "Generate new certificate with at least 2048-bit RSA or use ECDSA"
                });
                score -= 15;
            }
        }

        return score;
    }

    private async Task<int> AuditTrustedDevicesAsync(SecurityAuditResult auditResult, int score, CancellationToken cancellationToken)
    {
        var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
        
        if (trustedDevices.Count == 0)
        {
            auditResult.Warnings.Add(new SecurityIssue
            {
                Title = "No Trusted Devices",
                Description = "No trusted devices are configured",
                Severity = SecuritySeverity.Warning,
                AffectedComponent = "TrustedDevices"
            });
        }

        // Проверяем настройки безопасности устройств
        foreach (var device in trustedDevices)
        {
            if (!device.RequireTls13 && _securityConfig.GlobalRequireTls13)
            {
                auditResult.Warnings.Add(new SecurityIssue
                {
                    Title = "TLS 1.3 Not Required for Device",
                    Description = $"Device {device.DeviceId} does not require TLS 1.3",
                    Severity = SecuritySeverity.Warning,
                    AffectedComponent = $"Device {device.DeviceId}",
                    RecommendedAction = "Enable TLS 1.3 requirement for this device"
                });
                score -= 2;
            }
        }

        return score;
    }

    private async Task<int> AuditSecurityEventsAsync(SecurityAuditResult auditResult, int score, CancellationToken cancellationToken)
    {
        var recentEvents = await GetSecurityEventsAsync(DateTime.UtcNow.AddDays(-7), null, null, 1000, cancellationToken);
        
        // Анализируем подозрительную активность
        var failedConnections = recentEvents.Count(e => e.EventType == SecurityEventType.AuthenticationFailed);
        if (failedConnections > 50)
        {
            auditResult.CriticalIssues.Add(new SecurityIssue
            {
                Title = "High Number of Failed Connections",
                Description = $"{failedConnections} failed authentication attempts in the last 7 days",
                Severity = SecuritySeverity.Critical,
                AffectedComponent = "Authentication",
                RecommendedAction = "Investigate potential brute force attacks"
            });
            score -= 10;
        }

        var tlsErrors = recentEvents.Count(e => e.EventType == SecurityEventType.TlsHandshakeError);
        if (tlsErrors > 20)
        {
            auditResult.Warnings.Add(new SecurityIssue
            {
                Title = "TLS Handshake Errors",
                Description = $"{tlsErrors} TLS handshake errors in the last 7 days",
                Severity = SecuritySeverity.Warning,
                AffectedComponent = "TLS",
                RecommendedAction = "Check TLS configuration and client compatibility"
            });
            score -= 5;
        }

        return score;
    }

    private async Task<int> AuditNetworkSecurityAsync(SecurityAuditResult auditResult, int score, CancellationToken cancellationToken)
    {
        // Проверяем настройки rate limiting
        if (!_securityConfig.EnableConnectionRateLimit)
        {
            auditResult.Warnings.Add(new SecurityIssue
            {
                Title = "Connection Rate Limiting Disabled",
                Description = "Connection rate limiting is disabled",
                Severity = SecuritySeverity.Warning,
                AffectedComponent = "NetworkSecurity",
                RecommendedAction = "Enable connection rate limiting to prevent abuse"
            });
            score -= 3;
        }

        if (_securityConfig.MaxFailedConnections > 10)
        {
            auditResult.Warnings.Add(new SecurityIssue
            {
                Title = "High Failed Connection Threshold",
                Description = "Maximum failed connections threshold is too high",
                Severity = SecuritySeverity.Warning,
                AffectedComponent = "NetworkSecurity",
                RecommendedAction = "Reduce MaxFailedConnections to 5 or lower"
            });
            score -= 2;
        }

        return score;
    }

    private void GenerateRecommendations(SecurityAuditResult auditResult)
    {
        // Базовые рекомендации на основе текущих настроек
        auditResult.Recommendations.Add(new SecurityRecommendation
        {
            Title = "Regular Security Audits",
            Description = "Perform regular security audits to identify and address potential vulnerabilities",
            Priority = 8,
            ImplementationGuide = "Schedule automated security audits every 24 hours"
        });

        auditResult.Recommendations.Add(new SecurityRecommendation
        {
            Title = "Certificate Monitoring",
            Description = "Monitor certificate expiration dates and set up automated renewal",
            Priority = 9,
            ImplementationGuide = "Enable AutoRenewCertificates and set CertificateRenewalDaysThreshold to 30"
        });

        if (auditResult.CriticalIssues.Count > 0)
        {
            auditResult.Recommendations.Add(new SecurityRecommendation
            {
                Title = "Address Critical Issues",
                Description = "Immediately address all critical security issues found in the audit",
                Priority = 10,
                ImplementationGuide = "Review each critical issue and implement the recommended actions"
            });
        }

        auditResult.Recommendations.Add(new SecurityRecommendation
        {
            Title = "Upgrade to TLS 1.3",
            Description = "Use TLS 1.3 for all connections to ensure maximum security",
            Priority = 7,
            ImplementationGuide = "Set GlobalRequireTls13 = true in SecurityConfiguration"
        });
    }

    private async void FlushEventBuffer(object? state)
    {
        try
        {
            await FlushEventBufferAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing security event buffer");
        }
    }

    private async Task FlushEventBufferAsync(CancellationToken cancellationToken)
    {
        List<SecurityEvent> eventsToFlush;
        
        lock (_bufferLock)
        {
            if (_eventBuffer.Count == 0)
                return;

            eventsToFlush = new List<SecurityEvent>(_eventBuffer);
            _eventBuffer.Clear();
        }

        try
        {
            // TODO: Реализовать массовую запись в базу данных
            // await _database.BulkInsertSecurityEventsAsync(eventsToFlush, cancellationToken);
            
            _logger.LogDebug("Flushed {Count} security events to database", eventsToFlush.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing security events to database");
            
            // Возвращаем события обратно в буфер при ошибке
            lock (_bufferLock)
            {
                _eventBuffer.InsertRange(0, eventsToFlush);
            }
        }
    }

    public override void Dispose()
    {
        _flushTimer?.Dispose();
        base.Dispose();
    }
}