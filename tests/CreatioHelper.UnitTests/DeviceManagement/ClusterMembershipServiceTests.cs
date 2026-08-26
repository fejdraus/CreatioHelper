using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.DeviceManagement;
using CreatioHelper.Infrastructure.Services.Network.Discovery;
using CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.DeviceManagement;

public class ClusterMembershipServiceTests
{
    private const string LocalDeviceId = "LOCALID-AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG";

    [Fact]
    public void GetLocalMember_ReturnsLocalDeviceIdAndName()
    {
        var harness = new Harness();

        var member = harness.Service.GetLocalMember();

        Assert.Equal(LocalDeviceId, member.DeviceId);
        Assert.Equal("Local Agent", member.DeviceName);
    }

    [Fact]
    public void ResolveApiAddress_PicksFirstRoutableAddress()
    {
        var harness = new Harness();

        var resolved = harness.Service.ResolveApiAddress(
            "peer-1",
            new[] { "tcp://0.0.0.0:22000", "tcp://10.0.0.5:22000" });

        Assert.Equal("http://10.0.0.5:5275", resolved);
    }

    [Fact]
    public async Task AcceptPairedDeviceAsync_AddsRequestingDevice()
    {
        var harness = new Harness();

        await harness.Service.AcceptPairedDeviceAsync(new ClusterMember
        {
            DeviceId = "joiner",
            DeviceName = "Joiner",
            Addresses = new List<string> { "tcp://10.0.0.9:22000" },
            ApiAddress = "http://10.0.0.9:5275"
        });

        Assert.Contains(harness.AddedDevices, d => d.DeviceId == "joiner");
    }

    [Fact]
    public async Task AcceptPairedDeviceAsync_IgnoresSelf()
    {
        var harness = new Harness();

        await harness.Service.AcceptPairedDeviceAsync(new ClusterMember { DeviceId = LocalDeviceId });

        Assert.Empty(harness.AddedDevices);
    }

    private sealed class Harness
    {
        public List<SyncDevice> AddedDevices { get; } = new();
        public ClusterMembershipService Service { get; }

        public Harness()
        {
            var syncEngine = new Mock<ISyncEngine>();
            syncEngine.Setup(x => x.DeviceId).Returns(LocalDeviceId);
            syncEngine.Setup(x => x.GetDevicesAsync()).ReturnsAsync(() => new List<SyncDevice>());
            syncEngine.Setup(x => x.AddDeviceAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>()))
                .ReturnsAsync((string id, string name, string? _, List<string>? addresses) =>
                {
                    var device = new SyncDevice(id, name);
                    device.Addresses = addresses ?? new List<string>();
                    AddedDevices.Add(device);
                    return device;
                });

            var configManager = new Mock<CreatioHelper.Application.Interfaces.IConfigurationManager>();
            configManager.Setup(x => x.IsDeviceIgnored(It.IsAny<string>())).Returns(false);
            configManager.Setup(x => x.RemoveIgnoredDeviceAsync(It.IsAny<string>())).ReturnsAsync(true);
            configManager.Setup(x => x.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

            var pendingService = new Mock<IPendingService>();
            pendingService.Setup(x => x.RemovePendingDevice(It.IsAny<string>())).Returns(true);

            var httpClientFactory = new Mock<IHttpClientFactory>();
            var discoveryManager = new Mock<IDiscoveryManager>();
            var clusterKeyService = new Mock<IClusterKeyService>();

            var clusterConfig = new ClusterKeyConfiguration();
            var syncConfiguration = new SyncConfiguration(LocalDeviceId, "Local Agent");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ClusterKey:AgentHttpPort"] = "5275" })
                .Build();

            Service = new ClusterMembershipService(
                NullLogger<ClusterMembershipService>.Instance,
                clusterKeyService.Object,
                syncEngine.Object,
                configManager.Object,
                httpClientFactory.Object,
                discoveryManager.Object,
                pendingService.Object,
                Options.Create(clusterConfig),
                syncConfiguration,
                configuration);
        }
    }
}
