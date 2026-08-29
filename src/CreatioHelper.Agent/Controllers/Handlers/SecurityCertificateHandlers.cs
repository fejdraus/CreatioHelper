using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Agent.Controllers.Handlers;

internal sealed class SecurityCertificateHandlers
{
    private readonly ICertificateManager _certificateManager;
    private readonly ISecurityAuditor _securityAuditor;
    private readonly ILogger _logger;
    private readonly Func<DeviceSecurityConfiguration, CancellationToken, Task<X509Certificate2?>> _loadCertificateAsync;

    public SecurityCertificateHandlers(
        ICertificateManager certificateManager,
        ISecurityAuditor securityAuditor,
        ILogger logger,
        Func<DeviceSecurityConfiguration, CancellationToken, Task<X509Certificate2?>> loadCertificateAsync)
    {
        _certificateManager = certificateManager;
        _securityAuditor = securityAuditor;
        _logger = logger;
        _loadCertificateAsync = loadCertificateAsync;
    }

    public async Task<IActionResult> RenewCertificateAsync(
        string deviceId,
        RenewCertificateRequest request,
        CancellationToken cancellationToken)
    {
        var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
        var device = trustedDevices.FirstOrDefault(d => d.DeviceId == deviceId);

        if (device?.Certificate == null)
        {
            return new NotFoundObjectResult(new { error = "Device not found or certificate missing" });
        }

        var currentCertificate = await _loadCertificateAsync(device, cancellationToken);
        if (currentCertificate == null)
        {
            return new ObjectResult(new { error = "Could not load certificate from storage" })
            {
                StatusCode = 500
            };
        }

        var newCertificate = await _certificateManager.RenewCertificateIfNeededAsync(
            currentCertificate,
            request.CommonName,
            request.ValidityDays,
            request.RenewalThresholdDays,
            cancellationToken);

        var renewed = newCertificate?.Thumbprint != currentCertificate.Thumbprint;

        await _securityAuditor.LogSecurityEventAsync(new SecurityEvent
        {
            EventType = SecurityEventType.CertificateRenewed,
            DeviceId = deviceId,
            Severity = SecuritySeverity.Info,
            Message = renewed
                ? $"Certificate renewed for device {deviceId}"
                : $"Certificate renewal not needed for device {deviceId}"
        }, cancellationToken);

        return new OkObjectResult(new
        {
            success = true,
            message = renewed ? "Certificate renewed successfully" : "Certificate renewal not needed",
            renewed,
            renewalRequired = device.RequiresCertificateRenewal(request.RenewalThresholdDays)
        });
    }

    public async Task<IActionResult> ExportCertificateAsync(
        string deviceId,
        ExportCertificateRequest request,
        CancellationToken cancellationToken)
    {
        var trustedDevices = await _certificateManager.GetTrustedDevicesAsync(cancellationToken);
        var device = trustedDevices.FirstOrDefault(d => d.DeviceId == deviceId);

        if (device?.Certificate == null)
        {
            return new NotFoundObjectResult(new { error = "Device not found or certificate missing" });
        }

        var certificate = await _loadCertificateAsync(device, cancellationToken);
        if (certificate == null)
        {
            return new ObjectResult(new { error = "Could not load certificate from storage" })
            {
                StatusCode = 500
            };
        }

        var exportedData = await _certificateManager.ExportCertificateAsync(
            certificate, request.Format, request.Password, cancellationToken);

        await _securityAuditor.LogSecurityEventAsync(new SecurityEvent
        {
            EventType = SecurityEventType.ConfigurationChanged,
            DeviceId = deviceId,
            Severity = SecuritySeverity.Info,
            Message = $"Certificate exported for device {deviceId} in format {request.Format}"
        }, cancellationToken);

        return new OkObjectResult(new
        {
            success = true,
            message = "Certificate exported successfully",
            format = request.Format.ToString(),
            certificateData = Convert.ToBase64String(exportedData)
        });
    }
}
