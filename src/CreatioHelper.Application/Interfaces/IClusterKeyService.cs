namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Service for cluster key HMAC challenge-response auto-pairing.
/// The raw cluster key never leaves the agent — only HMAC proofs are exchanged.
/// </summary>
public interface IClusterKeyService
{
    /// <summary>
    /// Whether cluster key auto-pairing is enabled and configured.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Generate a challenge (nonce) for a requesting device.
    /// </summary>
    /// <param name="requestingDeviceId">Device ID of the agent requesting pairing</param>
    /// <returns>Challenge response with nonce and local device ID</returns>
    ChallengeResponse? GenerateChallenge(string requestingDeviceId);

    /// <summary>
    /// Verify an HMAC proof against a previously issued challenge.
    /// </summary>
    /// <param name="nonce">The nonce from the challenge</param>
    /// <param name="remoteDeviceId">Device ID of the remote agent</param>
    /// <param name="hmacProof">The HMAC-SHA256 proof computed by the remote agent</param>
    /// <returns>True if the proof is valid (same cluster key)</returns>
    bool VerifyChallenge(string nonce, string remoteDeviceId, string hmacProof);

    /// <summary>
    /// Compute an HMAC proof for sending to a remote agent.
    /// </summary>
    /// <param name="nonce">The nonce received from the remote agent</param>
    /// <param name="localDeviceId">This agent's device ID</param>
    /// <param name="remoteDeviceId">The remote agent's device ID</param>
    /// <returns>Base64-encoded HMAC-SHA256 proof</returns>
    string ComputeProof(string nonce, string localDeviceId, string remoteDeviceId);
}

/// <summary>
/// Response from a challenge request, containing the nonce and responder's device ID.
/// </summary>
public class ChallengeResponse
{
    public string Nonce { get; set; } = "";
    public string DeviceId { get; set; } = "";
}
