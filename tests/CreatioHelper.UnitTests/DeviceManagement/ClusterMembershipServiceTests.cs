using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.DeviceManagement;
using CreatioHelper.Infrastructure.Services.Network.Discovery;
using CreatioHelper.Infrastructure.Services.Sync.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CreatioHelper.UnitTests.DeviceManagement;

public class ClusterMembershipServiceTests
{
    private const string ClusterKey = "shared-group-key";

    [Fact]
    public async Task BuildRosterAsync_IncludesLocalMemberAndKnownDevices()
    {
        var harness = new Harness();
        harness.KnownDevices.Add(NewDevice("peer-1", "tcp://10.0.0.1:22000"));

        var roster = await harness.Service.BuildRosterAsync();

        Assert.Equal(2, roster.Count);
        Assert.Contains(roster, m => m.DeviceId == Harness.LocalDeviceId);
        Assert.Contains(roster, m => m.DeviceId == "peer-1");
    }

    [Fact]
    public async Task BuildRosterAsync_OmitsPeersWhenSharingDisabled()
    {
        var harness = new Harness(shareRoster: false);
        harness.KnownDevices.Add(NewDevice("peer-1", "tcp://10.0.0.1:22000"));

        var roster = await harness.Service.BuildRosterAsync();

        Assert.Single(roster);
        Assert.Equal(Harness.LocalDeviceId, roster[0].DeviceId);
    }

    [Fact]
    public async Task AcceptPairedDeviceAsync_AddsRequestingDevice()
    {
        var harness = new Harness();

        var ack = await harness.Service.AcceptPairedDeviceAsync(new ClusterMember
        {
            DeviceId = "joiner",
            DeviceName = "Joiner",
            Addresses = new List<string> { "tcp://10.0.0.9:22000" },
            ApiAddress = "http://10.0.0.9:5275"
        });

        Assert.Contains(harness.AddedDevices, d => d.DeviceId == "joiner");
        Assert.Equal(Harness.LocalDeviceId, ack.Self.DeviceId);
        Assert.Contains(ack.Roster, m => m.DeviceId == "joiner");
    }

    [Fact]
    public async Task AcceptPairedDeviceAsync_IgnoresSelf()
    {
        var harness = new Harness();

        await harness.Service.AcceptPairedDeviceAsync(new ClusterMember { DeviceId = Harness.LocalDeviceId });

        Assert.Empty(harness.AddedDevices);
    }

    [Fact]
    public async Task MergeRosterAsync_AddsUnknownDevicesOnce()
    {
        var harness = new Harness();
        var members = new[]
        {
            new ClusterMember { DeviceId = "peer-1", Addresses = new List<string> { "tcp://10.0.0.1:22000" } },
            new ClusterMember { DeviceId = "peer-1", Addresses = new List<string> { "tcp://10.0.0.1:22000" } },
            new ClusterMember { DeviceId = Harness.LocalDeviceId }
        };

        var added = await harness.Service.MergeRosterAsync(members);

        Assert.Equal(new[] { "peer-1" }, added);
        Assert.Single(harness.AddedDevices);
    }

    [Fact]
    public async Task MergeRosterAsync_MergesAddressesOfKnownDevice()
    {
        var harness = new Harness();
        var existing = NewDevice("peer-1", "tcp://10.0.0.1:22000");
        harness.KnownDevices.Add(existing);

        var added = await harness.Service.MergeRosterAsync(new[]
        {
            new ClusterMember { DeviceId = "peer-1", Addresses = new List<string> { "tcp://203.0.113.7:22000" } }
        });

        Assert.Empty(added);
        Assert.Empty(harness.AddedDevices);
        Assert.Contains("tcp://203.0.113.7:22000", existing.Addresses);
        Assert.Contains("tcp://10.0.0.1:22000", existing.Addresses);
    }

    [Fact]
    public async Task PairWithAsync_ReturnsRemoteIdentityAndRoster()
    {
        var harness = new Harness();
        var seed = harness.AddPeer("seed", "http://seed:5275");
        seed.Roster.Add(new ClusterMember { DeviceId = "peer-2", ApiAddress = "http://peer-2:5275" });

        var result = await harness.Service.PairWithAsync("http://seed:5275");

        Assert.True(result.Success);
        Assert.Equal("seed", result.Remote!.DeviceId);
        Assert.Contains(result.Roster, m => m.DeviceId == "peer-2");
        Assert.Contains(seed.Accepted, d => d.DeviceId == Harness.LocalDeviceId);
    }

    [Fact]
    public async Task PairWithAsync_FailsOnDifferentClusterKey()
    {
        var harness = new Harness();
        harness.AddPeer("seed", "http://seed:5275", clusterKey: "another-key");

        var result = await harness.Service.PairWithAsync("http://seed:5275");

        Assert.False(result.Success);
        Assert.Null(result.Remote);
    }

    [Fact]
    public async Task PairWithAsync_FailsWhenClusterKeyDisabled()
    {
        var harness = new Harness(enabled: false);
        harness.AddPeer("seed", "http://seed:5275");

        var result = await harness.Service.PairWithAsync("http://seed:5275");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task JoinClusterAsync_PairsWithEveryMemberAdvertisedBySeed()
    {
        var harness = new Harness(seeds: new[] { "http://seed:5275" });
        var seed = harness.AddPeer("seed", "http://seed:5275");
        harness.AddPeer("peer-2", "http://peer-2:5275");
        harness.AddPeer("peer-3", "http://peer-3:5275");

        seed.Roster.Add(new ClusterMember { DeviceId = "peer-2", ApiAddress = "http://peer-2:5275" });
        seed.Roster.Add(new ClusterMember { DeviceId = "peer-3", ApiAddress = "http://peer-3:5275" });

        var report = await harness.Service.JoinClusterAsync();

        Assert.Equal(3, report.TargetsPaired);
        Assert.Equal(new[] { "peer-2", "peer-3", "seed" }, report.AddedDeviceIds.OrderBy(x => x).ToArray());
        Assert.All(harness.Peers.Values, p => Assert.Contains(p.Accepted, d => d.DeviceId == Harness.LocalDeviceId));
    }

    [Fact]
    public async Task JoinClusterAsync_ContactsEachTargetOnce()
    {
        var harness = new Harness(seeds: new[] { "http://seed:5275", "http://seed:5275/" });
        var seed = harness.AddPeer("seed", "http://seed:5275");
        seed.Roster.Add(new ClusterMember { DeviceId = "seed", ApiAddress = "http://seed:5275" });

        var report = await harness.Service.JoinClusterAsync();

        Assert.Equal(1, report.TargetsContacted);
        Assert.Single(report.AddedDeviceIds);
    }

    [Fact]
    public async Task JoinClusterAsync_KeepsGoingWhenATargetTimesOut()
    {
        var harness = new Harness(seeds: new[] { "http://timeout:5275", "http://seed:5275" });
        harness.TimingOutHosts.Add("timeout:5275");
        harness.AddPeer("seed", "http://seed:5275");

        var report = await harness.Service.JoinClusterAsync();

        Assert.Equal(2, report.TargetsContacted);
        Assert.Equal(1, report.TargetsPaired);
        Assert.Contains("http://timeout:5275", report.FailedTargets);
        Assert.Equal(new[] { "seed" }, report.AddedDeviceIds);
    }

    [Fact]
    public async Task PairWithAsync_PropagatesCallerCancellation()
    {
        var harness = new Harness();
        harness.AddPeer("seed", "http://seed:5275");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.PairWithAsync("http://seed:5275", cts.Token));
    }

    [Fact]
    public async Task JoinClusterAsync_ReportsUnreachableSeeds()
    {
        var harness = new Harness(seeds: new[] { "http://offline:5275" });

        var report = await harness.Service.JoinClusterAsync();

        Assert.Equal(0, report.TargetsPaired);
        Assert.Single(report.FailedTargets);
        Assert.Empty(harness.AddedDevices);
    }

    [Fact]
    public async Task JoinClusterAsync_DoesNothingWhenDisabled()
    {
        var harness = new Harness(enabled: false, seeds: new[] { "http://seed:5275" });
        harness.AddPeer("seed", "http://seed:5275");

        var report = await harness.Service.JoinClusterAsync();

        Assert.Equal(0, report.TargetsContacted);
        Assert.Empty(harness.AddedDevices);
    }

    [Fact]
    public async Task JoinClusterAsync_StopsAtTargetLimit()
    {
        var harness = new Harness(seeds: new[] { "http://seed:5275" }, maxJoinTargets: 1);
        var seed = harness.AddPeer("seed", "http://seed:5275");
        harness.AddPeer("peer-2", "http://peer-2:5275");
        seed.Roster.Add(new ClusterMember { DeviceId = "peer-2", ApiAddress = "http://peer-2:5275" });

        var report = await harness.Service.JoinClusterAsync();

        Assert.True(report.TargetLimitReached);
        Assert.Equal(1, report.TargetsContacted);
    }

    [Theory]
    [InlineData("tcp://0.0.0.0:22000")]
    [InlineData("tcp://[::]:22000")]
    [InlineData("tcp://*:22000")]
    public void ResolveApiAddress_IgnoresWildcardListenAddresses(string address)
    {
        var harness = new Harness();

        Assert.Null(harness.Service.ResolveApiAddress("peer-1", new[] { address }));
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
    public async Task ResolveApiAddress_PrefersAddressLearnedWhilePairing()
    {
        var harness = new Harness();
        harness.AddPeer("seed", "http://seed:5275");

        await harness.Service.PairWithAsync("http://seed:5275");

        Assert.Equal("http://seed:5275", harness.Service.ResolveApiAddress("seed", new[] { "tcp://10.0.0.5:22000" }));
    }


    [Fact]
    public async Task JoinClusterAsync_PairsWithDiscoveredDevices()
    {
        var harness = new Harness();
        harness.AddPeer("discovered-peer", "http://10.0.0.42:5275");
        harness.DiscoveredDevices.Add(new DiscoveredDeviceInfo
        {
            DeviceId = "discovered-peer",
            Addresses = new List<string> { "tcp://10.0.0.42:22000" }
        });

        var report = await harness.Service.JoinClusterAsync();

        Assert.Equal(1, report.TargetsPaired);
        Assert.Contains("discovered-peer", report.AddedDeviceIds);
    }

    private static SyncDevice NewDevice(string deviceId, params string[] addresses)
    {
        var device = new SyncDevice(deviceId, deviceId);
        device.Addresses = addresses.ToList();
        return device;
    }

    private sealed class Harness
    {
        public const string LocalDeviceId = "local-device";

        public List<SyncDevice> KnownDevices { get; } = new();
        public List<SyncDevice> AddedDevices { get; } = new();
        public Dictionary<string, FakePeer> Peers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TimingOutHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DiscoveredDeviceInfo> DiscoveredDevices { get; } = new();
        public ClusterMembershipService Service { get; }

        public Harness(
            bool enabled = true,
            bool shareRoster = true,
            IEnumerable<string>? seeds = null,
            int maxJoinTargets = 256)
        {
            var clusterConfig = new ClusterKeyConfiguration
            {
                Enabled = enabled,
                Key = ClusterKey,
                SeedAddresses = seeds?.ToList() ?? new List<string>(),
                ShareRoster = shareRoster,
                MaxJoinTargets = maxJoinTargets
            };

            var syncEngine = new Mock<ISyncEngine>();
            syncEngine.Setup(x => x.DeviceId).Returns(LocalDeviceId);
            syncEngine.Setup(x => x.GetDevicesAsync())
                .ReturnsAsync(() => KnownDevices.Concat(AddedDevices).ToList());
            syncEngine.Setup(x => x.AddDeviceAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>()))
                .ReturnsAsync((string id, string name, string? _, List<string>? addresses) =>
                {
                    var device = new SyncDevice(id, name);
                    device.Addresses = addresses ?? new List<string>();
                    AddedDevices.Add(device);
                    return device;
                });

            var clusterKeyService = new ClusterKeyService(
                NullLogger<ClusterKeyService>.Instance,
                Options.Create(clusterConfig),
                syncEngine.Object);

            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(new PeerRouter(Peers, TimingOutHosts)));

            var syncConfiguration = new SyncConfiguration(LocalDeviceId, "Local Agent");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ClusterKey:AgentHttpPort"] = "5275" })
                .Build();

            var discoveryManager = new Mock<IDiscoveryManager>();
            discoveryManager.Setup(x => x.GetDiscoveredDevices())
                .Returns(() => DiscoveredDevices);

            Service = new ClusterMembershipService(
                NullLogger<ClusterMembershipService>.Instance,
                clusterKeyService,
                syncEngine.Object,
                httpClientFactory.Object,
                discoveryManager.Object,
                Options.Create(clusterConfig),
                syncConfiguration,
                configuration);
        }

        public FakePeer AddPeer(string deviceId, string apiAddress, string clusterKey = ClusterKey)
        {
            var peer = new FakePeer(deviceId, apiAddress, clusterKey);
            Peers[new Uri(apiAddress).Authority] = peer;
            return peer;
        }
    }

    private sealed class FakePeer
    {
        public FakePeer(string deviceId, string apiAddress, string clusterKey)
        {
            DeviceId = deviceId;
            ApiAddress = apiAddress;

            var syncEngine = new Mock<ISyncEngine>();
            syncEngine.Setup(x => x.DeviceId).Returns(deviceId);

            KeyService = new ClusterKeyService(
                NullLogger<ClusterKeyService>.Instance,
                Options.Create(new ClusterKeyConfiguration { Enabled = true, Key = clusterKey }),
                syncEngine.Object);
        }

        public string DeviceId { get; }
        public string ApiAddress { get; }
        public ClusterKeyService KeyService { get; }
        public List<ClusterMember> Roster { get; } = new();
        public List<ClusterMember> Accepted { get; } = new();
    }

    private sealed class PeerRouter : HttpMessageHandler
    {
        private readonly Dictionary<string, FakePeer> _peers;
        private readonly HashSet<string> _timingOutHosts;

        public PeerRouter(Dictionary<string, FakePeer> peers, HashSet<string> timingOutHosts)
        {
            _peers = peers;
            _timingOutHosts = timingOutHosts;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            if (_timingOutHosts.Contains(uri.Authority))
            {
                throw new TaskCanceledException(
                    $"The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.",
                    new TimeoutException());
            }

            if (!_peers.TryGetValue(uri.Authority, out var peer))
            {
                throw new HttpRequestException($"No agent listening on {uri.Authority}");
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var payload = JsonDocument.Parse(body);
            var root = payload.RootElement;

            if (uri.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal))
            {
                var challenge = peer.KeyService.GenerateChallenge(root.GetProperty("deviceId").GetString()!);
                return challenge == null
                    ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    : JsonResponse(challenge);
            }

            var remoteId = root.GetProperty("deviceId").GetString()!;
            var isValid = peer.KeyService.VerifyChallenge(
                root.GetProperty("nonce").GetString()!,
                remoteId,
                root.GetProperty("hmacProof").GetString()!);

            if (!isValid)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            peer.Accepted.Add(new ClusterMember
            {
                DeviceId = remoteId,
                DeviceName = root.GetProperty("deviceName").GetString() ?? remoteId,
                ApiAddress = root.GetProperty("apiAddress").GetString() ?? ""
            });

            return JsonResponse(new ClusterPairingAck
            {
                Self = new ClusterMember { DeviceId = peer.DeviceId, ApiAddress = peer.ApiAddress },
                Roster = peer.Roster.ToList()
            });
        }

        private static HttpResponseMessage JsonResponse(object value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value, value.GetType())
            };
        }
    }
}
