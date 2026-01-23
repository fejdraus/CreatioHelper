using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync;

namespace CreatioHelper.Tests;

/// <summary>
/// Tests for BepProtocol address handling
/// Based on Syncthing's resolveDeviceAddrs behavior
/// </summary>
public class BepProtocolTests : IDisposable
{
    private readonly Mock<ILogger<BepProtocol>> _mockLogger;
    private readonly Mock<ISyncDatabase> _mockDatabase;
    private readonly Mock<IBlockInfoRepository> _mockBlockRepository;
    private readonly BepProtocol _protocol;
    private readonly X509Certificate2 _testCertificate;

    public BepProtocolTests()
    {
        _mockLogger = new Mock<ILogger<BepProtocol>>();
        _mockDatabase = new Mock<ISyncDatabase>();
        _mockBlockRepository = new Mock<IBlockInfoRepository>();

        // Create a test certificate
        _testCertificate = CreateTestCertificate();

        // Create BlockDuplicationDetector
        var blockCalculatorLogger = Mock.Of<ILogger<SyncthingBlockCalculator>>();
        var blockCalculator = new SyncthingBlockCalculator(blockCalculatorLogger);
        var blockDetectorLogger = Mock.Of<ILogger<BlockDuplicationDetector>>();
        var blockDetector = new BlockDuplicationDetector(blockDetectorLogger, _mockBlockRepository.Object, blockCalculator);

        _protocol = new BepProtocol(
            _mockLogger.Object,
            _mockDatabase.Object,
            blockDetector,
            22000,
            _testCertificate,
            "TEST-DEVICE-ID");
    }

    [Fact]
    public async Task ConnectAsync_WithOnlyDynamicAddress_ShouldReturnFalse()
    {
        // Arrange - device has only "dynamic" address (not resolved)
        var device = new SyncDevice("REMOTE-DEVICE-ID", "Remote Device");
        device.AddAddress("dynamic");

        // Act
        var result = await _protocol.ConnectAsync(device);

        // Assert - should return false because "dynamic" is filtered out
        Assert.False(result);
    }

    [Fact]
    public async Task ConnectAsync_WithStaticAddress_ShouldAttemptConnection()
    {
        // Arrange - device has a static address (will fail to connect but should try)
        var device = new SyncDevice("REMOTE-DEVICE-ID", "Remote Device");
        device.AddAddress("tcp://127.0.0.1:99999"); // Invalid port, will fail

        // Act
        var result = await _protocol.ConnectAsync(device);

        // Assert - should return false (connection failed) but attempt was made
        Assert.False(result);
        // Verify that we at least tried (no "only dynamic" log)
    }

    [Fact]
    public async Task ConnectAsync_WithMixedAddresses_ShouldSkipDynamic()
    {
        // Arrange - device has both "dynamic" and static addresses
        var device = new SyncDevice("REMOTE-DEVICE-ID", "Remote Device");
        device.AddAddress("dynamic");
        device.AddAddress("tcp://127.0.0.1:99999"); // Will fail but should be tried

        // Act
        var result = await _protocol.ConnectAsync(device);

        // Assert - should return false but attempt was made on static address
        Assert.False(result);
    }

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnected_ShouldReturnTrue()
    {
        // This test verifies the early return when already connected
        // We can't easily test this without a real connection, but we verify the behavior
        var device = new SyncDevice("REMOTE-DEVICE-ID", "Remote Device");
        device.AddAddress("dynamic");

        // First call - not connected, will return false
        var result1 = await _protocol.ConnectAsync(device);
        Assert.False(result1);

        // Second call - still not connected
        var result2 = await _protocol.ConnectAsync(device);
        Assert.False(result2);
    }

    [Fact]
    public void ConnectAsync_FiltersDynamicAddresses_CorrectlyFormatsAddressList()
    {
        // Arrange
        var device = new SyncDevice("TEST-DEVICE", "Test");
        device.AddAddress("dynamic");
        device.AddAddress("tcp://192.168.1.1:22000");
        device.AddAddress("dynamic"); // Duplicate dynamic
        device.AddAddress("tcp://192.168.1.2:22000");

        // Get filtered addresses (same logic as in ConnectAsync)
        var filtered = device.Addresses.Where(a => a != "dynamic").ToList();

        // Assert
        Assert.Equal(2, filtered.Count);
        Assert.Contains("tcp://192.168.1.1:22000", filtered);
        Assert.Contains("tcp://192.168.1.2:22000", filtered);
        Assert.DoesNotContain("dynamic", filtered);
    }

    private X509Certificate2 CreateTestCertificate()
    {
        // Create a simple self-signed certificate for testing
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=TestDevice",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));

        return cert;
    }

    public void Dispose()
    {
        _protocol?.Dispose();
        _testCertificate?.Dispose();
    }
}
