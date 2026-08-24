using CreatioHelper.Agent.Authorization;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// REST API controller for cluster key HMAC challenge-response auto-pairing.
/// Challenge and verify endpoints are anonymous (inter-agent communication).
/// </summary>
[ApiController]
[Route("rest/cluster/key")]
public class ClusterKeyController : ControllerBase
{
    private readonly IClusterKeyService _clusterKeyService;
    private readonly IClusterMembershipService _membership;
    private readonly IPendingService _pendingService;
    private readonly ClusterKeyConfiguration _config;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<ClusterKeyController> _logger;

    public ClusterKeyController(
        IClusterKeyService clusterKeyService,
        IClusterMembershipService membership,
        IPendingService pendingService,
        ClusterKeyConfiguration config,
        IHubContext<SyncHub> hubContext,
        ILogger<ClusterKeyController> logger)
    {
        _clusterKeyService = clusterKeyService;
        _membership = membership;
        _pendingService = pendingService;
        _config = config;
        _hubContext = hubContext;
        _logger = logger;
    }

    [HttpGet("config")]
    [Authorize(Roles = Roles.Admin)]
    public IActionResult GetConfig()
    {
        return Ok(new
        {
            enabled = _config.Enabled,
            hasKey = !string.IsNullOrWhiteSpace(_config.Key),
            seedAddresses = _config.SeedAddresses,
            shareRoster = _config.ShareRoster,
            rosterSyncIntervalMinutes = _config.RosterSyncIntervalMinutes,
            autoAcceptDevices = _config.AutoAcceptDevices
        });
    }

    [HttpPut("config")]
    [Authorize(Roles = Roles.Admin)]
    public IActionResult SetConfig([FromBody] ClusterKeyConfigRequest request)
    {
        if (request.Enabled == true && string.IsNullOrWhiteSpace(request.Key) && string.IsNullOrWhiteSpace(_config.Key))
        {
            return BadRequest(new { error = "A key is required to enable cluster pairing" });
        }

        if (request.Key != null)
        {
            _config.Key = request.Key;
        }

        if (request.Enabled.HasValue)
        {
            _config.Enabled = request.Enabled.Value;
        }

        if (request.SeedAddresses != null)
        {
            _config.SeedAddresses = request.SeedAddresses;
        }

        if (request.AutoAcceptDevices.HasValue)
        {
            _config.AutoAcceptDevices = request.AutoAcceptDevices.Value;
        }

        _logger.LogInformation("Cluster key configuration updated at runtime (enabled={Enabled}, hasKey={HasKey}, seeds={Seeds}, autoAccept={AutoAccept})",
            _config.Enabled, !string.IsNullOrWhiteSpace(_config.Key), _config.SeedAddresses.Count, _config.AutoAcceptDevices);

        if (_membership.IsEnabled)
        {
            _ = Task.Run(() => _membership.JoinClusterAsync());
        }

        return Ok(new
        {
            enabled = _config.Enabled,
            hasKey = !string.IsNullOrWhiteSpace(_config.Key),
            seedAddresses = _config.SeedAddresses
        });
    }

    /// <summary>
    /// Request a challenge nonce for cluster key verification.
    /// Called by a remote agent that wants to prove it has the same cluster key.
    /// </summary>
    [HttpPost("challenge")]
    [AllowAnonymous]
    public IActionResult Challenge([FromBody] ChallengeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest(new { error = "DeviceId is required" });

        var challenge = _clusterKeyService.GenerateChallenge(request.DeviceId);
        if (challenge == null)
            return StatusCode(429, new { error = "Challenge rejected (disabled or rate-limited)" });

        return Ok(challenge);
    }

    /// <summary>
    /// Verify an HMAC proof against a previously issued challenge.
    /// If valid, the remote agent is auto-accepted.
    /// </summary>
    [HttpPost("verify")]
    [AllowAnonymous]
    public async Task<IActionResult> Verify([FromBody] VerifyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Nonce) ||
            string.IsNullOrWhiteSpace(request.DeviceId) ||
            string.IsNullOrWhiteSpace(request.HmacProof))
        {
            return BadRequest(new { error = "Nonce, DeviceId, and HmacProof are required" });
        }

        var isValid = _clusterKeyService.VerifyChallenge(request.Nonce, request.DeviceId, request.HmacProof);

        if (!isValid)
            return Unauthorized(new { error = "Cluster key verification failed" });

        var member = new ClusterMember
        {
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName ?? request.DeviceId,
            Addresses = request.Addresses ?? new List<string>(),
            ApiAddress = request.ApiAddress ?? ""
        };

        var decision = await _membership.RequestJoinAsync(member, cancellationToken);

        if (decision.Pending)
        {
            _logger.LogInformation("Cluster key verified for device {DeviceId}, awaiting approval", request.DeviceId);

            _pendingService.AddPendingDevice(new PendingDevice
            {
                DeviceId = request.DeviceId,
                Name = request.DeviceName ?? request.DeviceId,
                Address = request.Addresses?.FirstOrDefault(),
                Addresses = request.Addresses ?? new List<string>(),
                ApiAddress = request.ApiAddress
            });

            _ = _hubContext.Clients.Group("sync-events").SendAsync("ClusterDeviceJoinRequested", new
            {
                deviceId = request.DeviceId,
                timestamp = DateTime.UtcNow
            }, cancellationToken);

            return Ok(new ClusterPairingAck { Pending = true });
        }

        _logger.LogInformation("Cluster key verified for device {DeviceId}, admitted", request.DeviceId);

        _ = _hubContext.Clients.Group("sync-events").SendAsync("ClusterKeyPairingCompleted", new
        {
            deviceId = request.DeviceId,
            timestamp = DateTime.UtcNow
        }, cancellationToken);

        return Ok(decision.Ack);
    }

    [HttpPost("join")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Join(CancellationToken cancellationToken)
    {
        if (!_membership.IsEnabled)
        {
            return BadRequest(new { error = "Cluster key is not enabled" });
        }

        var report = await _membership.JoinClusterAsync(cancellationToken);
        return Ok(report);
    }

    [HttpGet("roster")]
    [Authorize(Roles = Roles.MonitorRoles)]
    public async Task<IActionResult> Roster(CancellationToken cancellationToken)
    {
        return Ok(await _membership.BuildRosterAsync(cancellationToken));
    }

    /// <summary>
    /// Get cluster key status (enabled/disabled). Does not expose the key itself.
    /// </summary>
    [HttpGet("status")]
    [Authorize(Roles = Roles.MonitorRoles)]
    public IActionResult Status()
    {
        return Ok(new { enabled = _clusterKeyService.IsEnabled });
    }
}

public class ClusterKeyConfigRequest
{
    public bool? Enabled { get; set; }

    public string? Key { get; set; }

    public List<string>? SeedAddresses { get; set; }

    public bool? AutoAcceptDevices { get; set; }
}

public class ChallengeRequest
{
    [Required]
    public string DeviceId { get; set; } = "";
}

public class VerifyRequest
{
    [Required]
    public string Nonce { get; set; } = "";

    [Required]
    public string DeviceId { get; set; } = "";

    [Required]
    public string HmacProof { get; set; } = "";

    public string? DeviceName { get; set; }

    public List<string>? Addresses { get; set; }

    public string? ApiAddress { get; set; }
}
