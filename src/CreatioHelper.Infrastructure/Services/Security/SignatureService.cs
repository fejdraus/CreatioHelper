using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Interface for cryptographic signature operations.
/// Used for release verification and secure update mechanisms.
/// </summary>
public interface ISignatureService
{
    /// <summary>
    /// Generates a new ECDSA key pair using the NIST P-521 curve.
    /// </summary>
    /// <returns>A tuple containing the private key and public key as byte arrays.</returns>
    (byte[] PrivateKey, byte[] PublicKey) GenerateKeys();

    /// <summary>
    /// Signs data using an ECDSA private key.
    /// </summary>
    /// <param name="privateKey">The private key in PKCS#8 format.</param>
    /// <param name="data">The data to sign.</param>
    /// <returns>The signature bytes.</returns>
    byte[] Sign(byte[] privateKey, byte[] data);

    /// <summary>
    /// Signs data using an ECDSA private key.
    /// </summary>
    /// <param name="privateKey">The private key in PKCS#8 format.</param>
    /// <param name="data">The data stream to sign.</param>
    /// <returns>The signature bytes.</returns>
    byte[] Sign(byte[] privateKey, Stream data);

    /// <summary>
    /// Verifies a signature using an ECDSA public key.
    /// </summary>
    /// <param name="publicKey">The public key in SubjectPublicKeyInfo format.</param>
    /// <param name="data">The data that was signed.</param>
    /// <param name="signature">The signature to verify.</param>
    /// <returns>True if the signature is valid, false otherwise.</returns>
    bool Verify(byte[] publicKey, byte[] data, byte[] signature);

    /// <summary>
    /// Verifies a signature using an ECDSA public key.
    /// </summary>
    /// <param name="publicKey">The public key in SubjectPublicKeyInfo format.</param>
    /// <param name="data">The data stream that was signed.</param>
    /// <param name="signature">The signature to verify.</param>
    /// <returns>True if the signature is valid, false otherwise.</returns>
    bool Verify(byte[] publicKey, Stream data, byte[] signature);

    /// <summary>
    /// Exports the public key in PEM format.
    /// </summary>
    /// <param name="publicKey">The public key bytes.</param>
    /// <returns>The PEM-encoded public key string.</returns>
    string ExportPublicKeyPem(byte[] publicKey);

    /// <summary>
    /// Imports a public key from PEM format.
    /// </summary>
    /// <param name="pem">The PEM-encoded public key string.</param>
    /// <returns>The public key bytes.</returns>
    byte[] ImportPublicKeyPem(string pem);

    /// <summary>
    /// Verifies a signature using a PEM-encoded public key.
    /// </summary>
    /// <param name="publicKeyPem">The PEM-encoded public key.</param>
    /// <param name="data">The data that was signed.</param>
    /// <param name="signature">The signature to verify.</param>
    /// <returns>True if the signature is valid, false otherwise.</returns>
    bool VerifyWithPem(string publicKeyPem, byte[] data, byte[] signature);
}

/// <summary>
/// Provides ECDSA signature operations for release verification.
/// </summary>
/// <remarks>
/// Mirrors the functionality of lib/signature in Syncthing:
/// - Uses ECDSA with NIST P-521 curve (same as Syncthing)
/// - Signs with SHA-256 hash algorithm
/// - Provides key generation, signing, and verification
///
/// This is used for verifying release binaries and updates to ensure
/// they haven't been tampered with.
/// </remarks>
public class SignatureService : ISignatureService
{
    private readonly ILogger<SignatureService>? _logger;

    public SignatureService(ILogger<SignatureService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public (byte[] PrivateKey, byte[] PublicKey) GenerateKeys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP521);

        var privateKey = ecdsa.ExportPkcs8PrivateKey();
        var publicKey = ecdsa.ExportSubjectPublicKeyInfo();

        _logger?.LogDebug("Generated new ECDSA P-521 key pair");

        return (privateKey, publicKey);
    }

    /// <inheritdoc/>
    public byte[] Sign(byte[] privateKey, byte[] data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(privateKey, out _);

        var signature = ecdsa.SignData(data, HashAlgorithmName.SHA256);

        _logger?.LogDebug("Signed {DataLength} bytes of data", data.Length);

        return signature;
    }

    /// <inheritdoc/>
    public byte[] Sign(byte[] privateKey, Stream data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(privateKey, out _);

        var signature = ecdsa.SignData(data, HashAlgorithmName.SHA256);

        _logger?.LogDebug("Signed stream data");

        return signature;
    }

    /// <inheritdoc/>
    public bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);

            var isValid = ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);

            _logger?.LogDebug(
                "Signature verification {Result} for {DataLength} bytes",
                isValid ? "succeeded" : "failed",
                data.Length);

            return isValid;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            _logger?.LogWarning(ex, "Signature verification failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Verify(byte[] publicKey, Stream data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);

            var isValid = ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);

            _logger?.LogDebug("Stream signature verification {Result}", isValid ? "succeeded" : "failed");

            return isValid;
        }
        catch (CryptographicException ex)
        {
            _logger?.LogWarning(ex, "Stream signature verification failed with cryptographic error");
            return false;
        }
    }

    /// <inheritdoc/>
    public string ExportPublicKeyPem(byte[] publicKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);

        return ecdsa.ExportSubjectPublicKeyInfoPem();
    }

    /// <inheritdoc/>
    public byte[] ImportPublicKeyPem(string pem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);

        return ecdsa.ExportSubjectPublicKeyInfo();
    }

    /// <inheritdoc/>
    public bool VerifyWithPem(string publicKeyPem, byte[] data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);

            var isValid = ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);

            _logger?.LogDebug(
                "PEM signature verification {Result} for {DataLength} bytes",
                isValid ? "succeeded" : "failed",
                data.Length);

            return isValid;
        }
        catch (CryptographicException ex)
        {
            _logger?.LogWarning(ex, "PEM signature verification failed with cryptographic error");
            return false;
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "PEM signature verification failed due to invalid PEM format");
            return false;
        }
    }
}

/// <summary>
/// Extension methods for signature verification of files and releases.
/// </summary>
public static class SignatureServiceExtensions
{
    /// <summary>
    /// Verifies the signature of a file.
    /// </summary>
    public static async Task<bool> VerifyFileAsync(
        this ISignatureService signatureService,
        byte[] publicKey,
        string filePath,
        byte[] signature,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        return signatureService.Verify(publicKey, stream, signature);
    }

    /// <summary>
    /// Signs a file and returns the signature.
    /// </summary>
    public static async Task<byte[]> SignFileAsync(
        this ISignatureService signatureService,
        byte[] privateKey,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        return signatureService.Sign(privateKey, stream);
    }

    /// <summary>
    /// Verifies the signature of a file using a PEM-encoded public key.
    /// </summary>
    public static async Task<bool> VerifyFileWithPemAsync(
        this ISignatureService signatureService,
        string publicKeyPem,
        string filePath,
        byte[] signature,
        CancellationToken cancellationToken = default)
    {
        var publicKey = signatureService.ImportPublicKeyPem(publicKeyPem);
        return await signatureService.VerifyFileAsync(publicKey, filePath, signature, cancellationToken);
    }
}
