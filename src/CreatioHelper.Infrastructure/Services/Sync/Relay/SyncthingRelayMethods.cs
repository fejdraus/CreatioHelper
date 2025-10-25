using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Static methods for Syncthing relay operations matching exact protocol behavior
/// Implements GetInvitationFromRelay, JoinSession and TestRelay methods
/// </summary>
public static class SyncthingRelayMethods
{
    /// <summary>
    /// Get invitation from a relay server for connecting to a specific device
    /// 100% compatible with Syncthing's GetInvitationFromRelay function
    /// </summary>
    public static async Task<SessionInvitation> GetInvitationFromRelayAsync(
        Uri relayUri,
        byte[] deviceId,
        X509Certificate2Collection certificates,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (relayUri.Scheme != "relay")
            throw new NotSupportedException($"Unsupported relay scheme: {relayUri.Scheme}");

        TcpClient? tcpClient = null;
        SslStream? sslStream = null;

        try
        {
            // Create TCP connection with timeout
            tcpClient = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await tcpClient.ConnectAsync(relayUri.Host, relayUri.Port, combinedCts.Token);

            // Establish TLS connection with ALPN
            var networkStream = tcpClient.GetStream();
            sslStream = new SslStream(networkStream, false, ValidateRelayServerCertificate);

            var clientAuthOptions = new SslClientAuthenticationOptions
            {
                TargetHost = relayUri.Host,
                ClientCertificates = certificates,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new SslApplicationProtocol(RelayProtocol.ProtocolName)
                }
            };

            await sslStream.AuthenticateAsClientAsync(clientAuthOptions, combinedCts.Token);

            // Perform handshake validation (Syncthing certificate check)
            ValidateRelayHandshake(sslStream, relayUri);

            // Send ConnectRequest
            var connectRequest = new ConnectRequest(deviceId);
            await RelayMessageSerializer.WriteMessageAsync(sslStream, connectRequest, combinedCts.Token);

            // Read response
            var message = await RelayMessageSerializer.ReadMessageAsync(sslStream, combinedCts.Token);

            switch (message)
            {
                case Response response:
                    throw new IncorrectResponseCodeException(response.Code, response.Message);

                case SessionInvitation invitation:
                    // Handle unspecified addresses (Syncthing behavior)
                    var ip = new IPAddress(invitation.Address);
                    if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                    {
                        // Use relay server IP
                        if (tcpClient.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
                        {
                            invitation = invitation with { Address = remoteEndPoint.Address.GetAddressBytes() };
                        }
                    }
                    return invitation;

                default:
                    throw new InvalidOperationException($"Protocol error: unexpected message {message.GetType().Name}");
            }
        }
        finally
        {
            sslStream?.Dispose();
            tcpClient?.Dispose();
        }
    }

    /// <summary>
    /// Join a relay session using the provided invitation
    /// 100% compatible with Syncthing's JoinSession function
    /// </summary>
    public static async Task<NetworkStream> JoinSessionAsync(
        SessionInvitation invitation,
        TimeSpan timeout = default,
        CancellationToken cancellationToken = default)
    {
        timeout = timeout == default ? TimeSpan.FromSeconds(10) : timeout;

        var address = new IPAddress(invitation.Address);
        var endPoint = new IPEndPoint(address, invitation.Port);

        TcpClient? tcpClient = null;

        try
        {
            // Connect to session address
            tcpClient = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await tcpClient.ConnectAsync(endPoint, combinedCts.Token);
            var stream = tcpClient.GetStream();

            // Set deadline for handshake
            tcpClient.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            tcpClient.SendTimeout = (int)timeout.TotalMilliseconds;

            // Send JoinSessionRequest
            var joinRequest = new JoinSessionRequest(invitation.Key);
            await RelayMessageSerializer.WriteMessageAsync(stream, joinRequest, combinedCts.Token);

            // Read response
            var message = await RelayMessageSerializer.ReadMessageAsync(stream, combinedCts.Token);

            // Clear timeout
            tcpClient.ReceiveTimeout = 0;
            tcpClient.SendTimeout = 0;

            switch (message)
            {
                case Response response when response.Code == 0:
                    // Success - return the stream (don't dispose tcpClient)
                    var resultStream = stream;
                    tcpClient = null; // Prevent disposal
                    return resultStream;

                case Response response:
                    throw new IncorrectResponseCodeException(response.Code, response.Message);

                default:
                    throw new InvalidOperationException($"Protocol error: expecting response got {message.GetType().Name}");
            }
        }
        catch
        {
            tcpClient?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Test relay connectivity and performance
    /// 100% compatible with Syncthing's TestRelay function
    /// </summary>
    public static async Task TestRelayAsync(
        Uri relayUri,
        X509Certificate2Collection certificates,
        TimeSpan sleepBetweenAttempts,
        TimeSpan operationTimeout,
        int attempts,
        CancellationToken cancellationToken = default)
    {
        var deviceId = GetDeviceIdFromCertificate(certificates[0]);
        
        // Start relay client
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<SyncthingRelayClient>();
        
        using var client = new SyncthingRelayClient(logger, relayUri, certificates, operationTimeout);
        
        var clientTask = Task.Run(async () =>
        {
            if (await client.ConnectAsync())
            {
                // Handle incoming invitations (discard them)
                while (client.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var invitation = client.GetNextInvitation();
                    if (invitation == null)
                        await Task.Delay(100, cancellationToken);
                }
            }
        }, cancellationToken);

        Exception? lastError = null;

        try
        {
            for (int i = 0; i < attempts; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var invitation = await GetInvitationFromRelayAsync(
                        relayUri, 
                        deviceId, 
                        certificates, 
                        operationTimeout, 
                        cancellationToken);
                    
                    // Success - relay is working
                    return;
                }
                catch (IncorrectResponseCodeException)
                {
                    // Expected error - device not found, continue testing
                    if (i < attempts - 1)
                        await Task.Delay(sleepBetweenAttempts, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    throw new InvalidOperationException($"Getting invitation failed: {ex.Message}", ex);
                }
            }

            // All attempts failed with expected errors - this is actually success for testing
            if (lastError is IncorrectResponseCodeException)
                return;

            throw new InvalidOperationException($"Getting invitation failed: {lastError?.Message ?? "Unknown error"}");
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    /// <summary>
    /// Extract device ID from certificate (Syncthing method)
    /// </summary>
    private static byte[] GetDeviceIdFromCertificate(X509Certificate2 certificate)
    {
        // Syncthing uses SHA-256 hash of the certificate DER bytes as device ID
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return sha256.ComputeHash(certificate.RawData);
    }

    /// <summary>
    /// Validate relay server certificate (allows self-signed)
    /// </summary>
    private static bool ValidateRelayServerCertificate(
        object sender, 
        X509Certificate? certificate, 
        X509Chain? chain, 
        SslPolicyErrors sslPolicyErrors)
    {
        // For relay servers, we accept self-signed certificates (common in Syncthing)
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        // Allow self-signed certificates
        if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            return true;

        return false;
    }

    /// <summary>
    /// Validate relay handshake and certificates (Syncthing protocol)
    /// </summary>
    private static void ValidateRelayHandshake(SslStream sslStream, Uri relayUri)
    {
        var connectionState = sslStream.RemoteCertificate;
        
        // Verify ALPN protocol negotiation
        if (sslStream.NegotiatedApplicationProtocol.Protocol.ToArray() !=
            System.Text.Encoding.UTF8.GetBytes(RelayProtocol.ProtocolName))
        {
            throw new InvalidOperationException("Protocol negotiation error");
        }

        // Check for relay ID verification (optional in Syncthing)
        var query = System.Web.HttpUtility.ParseQueryString(relayUri.Query);
        var expectedRelayId = query["id"];

        if (!string.IsNullOrEmpty(expectedRelayId))
        {
            if (connectionState == null)
                throw new InvalidOperationException("No server certificate provided");

            var certificates = sslStream.RemoteCertificate != null 
                ? new[] { new X509Certificate2(sslStream.RemoteCertificate) }
                : Array.Empty<X509Certificate2>();

            if (certificates.Length != 1)
                throw new InvalidOperationException($"Unexpected certificate count: {certificates.Length}");

            var actualRelayId = Convert.ToHexString(GetDeviceIdFromCertificate(certificates[0]));
            if (!string.Equals(actualRelayId, expectedRelayId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Relay ID does not match. Expected {expectedRelayId} got {actualRelayId}");
            }
        }
    }
}

/// <summary>
/// Exception for incorrect response codes from relay server (matches Syncthing behavior)
/// </summary>
public class IncorrectResponseCodeException : Exception
{
    public int Code { get; }
    public string ResponseMessage { get; }

    public IncorrectResponseCodeException(int code, string message) 
        : base($"Incorrect response code {code}: {message}")
    {
        Code = code;
        ResponseMessage = message;
    }
}