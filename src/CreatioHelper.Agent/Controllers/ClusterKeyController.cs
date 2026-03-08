using CreatioHelper.Agent.Authorization;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Application.Interfaces;
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
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<ClusterKeyController> _logger;

    public ClusterKeyController(
        IClusterKeyService clusterKeyService,
        IHubContext<SyncHub> hubContext,
        ILogger<ClusterKeyController> logger)
    {
        _clusterKeyService = clusterKeyService;
        _hubContext = hubContext;
        _logger = logger;
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
    public IActionResult Verify([FromBody] VerifyRequest request)
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

        _logger.LogInformation("Cluster key verified for device {DeviceId}, auto-accepting", request.DeviceId);

        // Notify connected UI clients via SignalR
        _ = _hubContext.Clients.Group("sync-events").SendAsync("ClusterKeyPairingCompleted", new
        {
            deviceId = request.DeviceId,
            timestamp = DateTime.UtcNow
        });

        return Ok(new { success = true, message = "Cluster key verified, device accepted" });
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
}
