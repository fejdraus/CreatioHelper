using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CreatioHelper.Agent.Tests;

public class SecurityControllerTests
{
    private readonly Mock<ICertificateManager> _certificateManager = new(MockBehavior.Strict);
    private readonly Mock<ISecurityAuditor> _securityAuditor = new(MockBehavior.Strict);
    private readonly SecurityController _sut;

    public SecurityControllerTests()
    {
        _sut = new SecurityController(
            _certificateManager.Object,
            _securityAuditor.Object,
            NullLogger<SecurityController>.Instance);
    }


    [Fact]
    public async Task GetSecurityConfiguration_ReturnsAggregatedCounters()
    {
        var statistics = new SecurityStatistics
        {
            TotalSecurityEvents = 0,
            EventsLast24Hours = 5,
            CertificatesRequiringRenewal = 2,
            LastSecurityAudit = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _certificateManager.Setup(m => m.GetTrustedDevicesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceSecurityConfiguration>
            {
                NewDevice("dev-1"),
                NewDevice("dev-2"),
                NewDevice("dev-3")
            });
        _securityAuditor.Setup(a => a.GetSecurityStatisticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(statistics);

        var result = await _sut.GetSecurityConfiguration();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task GetSecurityConfiguration_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _certificateManager.Setup(m => m.GetTrustedDevicesAsync(cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.GetSecurityConfiguration(cts.Token));
    }


    [Fact]
    public async Task GetCertificates_MapsAllTrustedDevices()
    {
        var device = NewDevice("dev-A");
        device.AllowAutoConnect = false;
        device.RequireTls13 = false;
        _certificateManager.Setup(m => m.GetTrustedDevicesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceSecurityConfiguration> { device });

        var result = await _sut.GetCertificates();

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
        Assert.Single(items);
    }

    [Fact]
    public async Task GetCertificates_EmptyList_ReturnsEmptyResult()
    {
        _certificateManager.Setup(m => m.GetTrustedDevicesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceSecurityConfiguration>());

        var result = await _sut.GetCertificates();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty((IEnumerable<object>)ok.Value!);
    }


    [Fact]
    public async Task CreateCertificate_LogsSecurityEvent()
    {
        var certificate = CreateSelfSignedCertificate("test-device");
        var info = new CertificateInfo
        {
            Fingerprint = "ABCD",
            Subject = "CN=test-device",
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(365),
            SignatureAlgorithm = CertificateSignatureAlgorithm.ECDSA,
            KeySize = 256
        };
        _certificateManager.Setup(m => m.CreateDeviceCertificateAsync(
                "test-device", 365, CertificateSignatureAlgorithm.ECDSA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(certificate);
        _certificateManager.Setup(m => m.ComputeDeviceId(certificate))
            .Returns("device-id-xyz");
        _certificateManager.Setup(m => m.GetCertificateInfo(certificate))
            .Returns(info);
        _securityAuditor.Setup(a => a.LogSecurityEventAsync(It.IsAny<SecurityEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateCertificate(new CreateCertificateRequest
        {
            CommonName = "test-device",
            ValidityDays = 365
        });

        Assert.IsType<OkObjectResult>(result);
        _securityAuditor.Verify(a => a.LogSecurityEventAsync(
            It.Is<SecurityEvent>(e => e.EventType == SecurityEventType.CertificateCreated
                                      && e.Severity == SecuritySeverity.Info),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateCertificate_PropagatesManagerException()
    {
        _certificateManager.Setup(m => m.CreateDeviceCertificateAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CertificateSignatureAlgorithm>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cert failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CreateCertificate(new CreateCertificateRequest
            {
                CommonName = "broken",
                ValidityDays = 30
            }));
        _securityAuditor.Verify(a => a.LogSecurityEventAsync(
            It.IsAny<SecurityEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }


    [Fact]
    public async Task ValidateCertificate_InvalidBase64_ReturnsBadRequest()
    {
        var result = await _sut.ValidateCertificate(new ValidateCertificateRequest
        {
            CertificateBase64 = "not-base64!!!"
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(bad.Value);
        _certificateManager.Verify(
            m => m.ValidateCertificateAsync(It.IsAny<X509Certificate2>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ValidateCertificate_ValidCertificate_ReturnsValidationResult()
    {
        var cert = CreateSelfSignedCertificate("validate-me");
        var certBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));
        var validation = new CertificateValidationResult
        {
            IsValid = true
        };
        _certificateManager.Setup(m => m.ValidateCertificateAsync(
                It.IsAny<X509Certificate2>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validation);

        var result = await _sut.ValidateCertificate(new ValidateCertificateRequest
        {
            CertificateBase64 = certBase64
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(validation, ok.Value);
    }


    [Fact]
    public async Task AddTrustedDevice_InvalidCertificate_ReturnsBadRequest()
    {
        var result = await _sut.AddTrustedDevice(new AddTrustedDeviceRequest
        {
            DeviceName = "broken",
            CertificateBase64 = "!!!not-base64"
        });

        Assert.IsType<BadRequestObjectResult>(result);
        _certificateManager.Verify(
            m => m.AddTrustedDeviceAsync(It.IsAny<string>(), It.IsAny<X509Certificate2>(),
                It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddTrustedDevice_ValidCertificate_AddsAndLogs()
    {
        var cert = CreateSelfSignedCertificate("trusted-device");
        var certBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));
        _certificateManager.Setup(m => m.ComputeDeviceId(It.IsAny<X509Certificate2>()))
            .Returns("trusted-device-id");
        _certificateManager.Setup(m => m.AddTrustedDeviceAsync(
                "trusted-device-id", It.IsAny<X509Certificate2>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _securityAuditor.Setup(a => a.LogSecurityEventAsync(
                It.IsAny<SecurityEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.AddTrustedDevice(new AddTrustedDeviceRequest
        {
            DeviceName = "trusted-device",
            CertificateBase64 = certBase64
        });

        Assert.IsType<OkObjectResult>(result);
        _certificateManager.Verify(m => m.AddTrustedDeviceAsync(
            "trusted-device-id", It.IsAny<X509Certificate2>(), It.IsAny<CancellationToken>()), Times.Once);
        _securityAuditor.Verify(a => a.LogSecurityEventAsync(
            It.Is<SecurityEvent>(e => e.EventType == SecurityEventType.ConfigurationChanged
                                      && e.DeviceId == "trusted-device-id"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveTrustedDevice_CallsManagerAndLogsRemoval()
    {
        _certificateManager.Setup(m => m.RemoveTrustedDeviceAsync("dev-X", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _securityAuditor.Setup(a => a.LogSecurityEventAsync(
                It.Is<SecurityEvent>(e => e.EventType == SecurityEventType.ConfigurationChanged
                                          && e.DeviceId == "dev-X"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.RemoveTrustedDevice("dev-X");

        Assert.IsType<OkObjectResult>(result);
        _certificateManager.Verify(m => m.RemoveTrustedDeviceAsync("dev-X", It.IsAny<CancellationToken>()),
            Times.Once);
        _securityAuditor.Verify(a => a.LogSecurityEventAsync(
            It.IsAny<SecurityEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }


    [Fact]
    public async Task PerformSecurityAudit_ReturnsAuditorResult()
    {
        var auditResult = new SecurityAuditResult
        {
            SecurityScore = 85,
            AuditTimestamp = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _securityAuditor.Setup(a => a.PerformSecurityAuditAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditResult);

        var result = await _sut.PerformSecurityAudit();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(auditResult, ok.Value);
    }


    [Fact]
    public async Task GetSecurityStatistics_ReturnsAuditorStatistics()
    {
        var stats = new SecurityStatistics
        {
            TotalSecurityEvents = 10,
            EventsLast24Hours = 3,
            TrustedDevices = 7,
            CertificatesRequiringRenewal = 1
        };
        _securityAuditor.Setup(a => a.GetSecurityStatisticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(stats);

        var result = await _sut.GetSecurityStatistics();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(stats, ok.Value);
    }


    [Theory]
    [InlineData(50, 50)]
    [InlineData(500, 500)]
    [InlineData(5_000, 1_000)]
    public async Task GetSecurityEvents_ClampsLimitToMaxThousand(int requested, int expectedUsed)
    {
        var captured = 0;
        _securityAuditor.Setup(a => a.GetSecurityEventsAsync(
                It.IsAny<DateTime?>(), It.IsAny<SecurityEventType?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime?, SecurityEventType?, string?, int, CancellationToken>(
                (_, _, _, limit, _) => captured = limit)
            .ReturnsAsync(new List<SecurityEvent>());

        await _sut.GetSecurityEvents(limit: requested);

        Assert.Equal(expectedUsed, captured);
    }

    [Fact]
    public async Task GetSecurityEvents_PassesFiltersThrough()
    {
        DateTime? since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const SecurityEventType eventType = SecurityEventType.CertificateCreated;
        const string deviceId = "filter-dev";
        _securityAuditor.Setup(a => a.GetSecurityEventsAsync(
                since, eventType, deviceId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SecurityEvent>());

        var result = await _sut.GetSecurityEvents(
            since: since, eventType: eventType, deviceId: deviceId, limit: 10);

        Assert.IsType<OkObjectResult>(result);
        _securityAuditor.Verify(a => a.GetSecurityEventsAsync(
            since, eventType, deviceId, 10, It.IsAny<CancellationToken>()), Times.Once);
    }


    [Theory]
    [InlineData(30, 30)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1_000, 365)]
    public async Task CleanupSecurityEvents_ClampsMaxAgeBetweenOneAndThreeSixtyFive(int requested, int expectedDays)
    {
        TimeSpan? captured = null;
        _securityAuditor.Setup(a => a.CleanupOldEventsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((age, _) => captured = age)
            .Returns(Task.CompletedTask);

        await _sut.CleanupSecurityEvents(new CleanupEventsRequest { MaxAgeDays = requested });

        Assert.NotNull(captured);
        Assert.Equal(expectedDays, captured!.Value.TotalDays);
    }


    private static DeviceSecurityConfiguration NewDevice(string id) => new()
    {
        DeviceId = id,
        IsTrusted = true,
        AllowAutoConnect = true,
        RequireTls13 = true
    };

    private static X509Certificate2 CreateSelfSignedCertificate(string cn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        rsa.Dispose();
        return cert;
    }
}