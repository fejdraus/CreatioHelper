using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Infrastructure.Services.Sync;
using System.IO;

namespace CreatioHelper.Tests;

public class KeySyncTests : IDisposable
{
    private readonly string _testKeyStorePath1;
    private readonly string _testKeyStorePath2;

    public KeySyncTests()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "CreatioHelper_KeySync_Tests", Guid.NewGuid().ToString());
        _testKeyStorePath1 = Path.Combine(testDir, "agent1");
        _testKeyStorePath2 = Path.Combine(testDir, "agent2");
        
        Directory.CreateDirectory(Path.GetDirectoryName(_testKeyStorePath1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_testKeyStorePath2)!);
    }

    public void Dispose()
    {
        try
        {
            var testDir = Path.GetDirectoryName(Path.GetDirectoryName(_testKeyStorePath1));
            if (testDir != null && Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public async Task TwoAgents_SameDeviceIds_GenerateSameKeys()
    {
        // Arrange - simulate two agents with their device IDs
        var mockLogger1 = new Mock<ILogger<KeyManager>>();
        var mockLogger2 = new Mock<ILogger<KeyManager>>();
        
        // Test the fixed implementation with canonical ordering
        Environment.SetEnvironmentVariable("Sync__DeviceId", "TEST-AGENT-1");
        var keyManager1 = new KeyManager(mockLogger1.Object, _testKeyStorePath1);
        var key1_for_agent2 = await keyManager1.GetOrCreateDeviceKeyAsync("TEST-AGENT-2");
        
        Environment.SetEnvironmentVariable("Sync__DeviceId", "TEST-AGENT-2");
        var keyManager2 = new KeyManager(mockLogger2.Object, _testKeyStorePath2);
        var key2_for_agent1 = await keyManager2.GetOrCreateDeviceKeyAsync("TEST-AGENT-1");
        
        // Reset environment to clean up
        Environment.SetEnvironmentVariable("Sync__DeviceId", null);
        
        // Assert - with canonical ordering, both should generate the same key
        Assert.Equal(32, key1_for_agent2.Length);
        Assert.Equal(32, key2_for_agent1.Length);
        
        
        // This should now PASS with the fixed canonical ordering implementation
        Assert.Equal(key1_for_agent2, key2_for_agent1);
    }

    [Fact]
    public async Task KeyGeneration_UsesCanonicalOrdering_ShouldWork()
    {
        // This test verifies what the fixed implementation should do
        var device1 = "TEST-AGENT-1";
        var device2 = "TEST-AGENT-2";
        
        // Both agents should use the same canonical key material regardless of who generates it
        var canonicalMaterial1 = string.Compare(device1, device2, StringComparison.OrdinalIgnoreCase) < 0 
            ? $"{device1}-{device2}".ToLowerInvariant()
            : $"{device2}-{device1}".ToLowerInvariant();
            
        var canonicalMaterial2 = string.Compare(device2, device1, StringComparison.OrdinalIgnoreCase) < 0 
            ? $"{device2}-{device1}".ToLowerInvariant() 
            : $"{device1}-{device2}".ToLowerInvariant();
            
        // Both should be the same
        Assert.Equal(canonicalMaterial1, canonicalMaterial2);
        Assert.Equal("test-agent-1-test-agent-2", canonicalMaterial1);
    }
}