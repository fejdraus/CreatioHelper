namespace CreatioHelper.Application.Interfaces;

public interface IClusterMembershipService
{
    bool IsEnabled { get; }

    ClusterMember GetLocalMember();

    Task<List<ClusterMember>> BuildRosterAsync(CancellationToken cancellationToken = default);

    Task<ClusterPairingAck> AcceptPairedDeviceAsync(ClusterMember remote, CancellationToken cancellationToken = default);

    Task<ClusterPairingResult> PairWithAsync(string apiBaseUrl, CancellationToken cancellationToken = default);

    Task<ClusterJoinReport> JoinClusterAsync(CancellationToken cancellationToken = default);

    Task<List<string>> MergeRosterAsync(IEnumerable<ClusterMember> members, CancellationToken cancellationToken = default);

    string? ResolveApiAddress(string deviceId, IEnumerable<string>? addresses = null);
}

public class ClusterMember
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    public string ApiAddress { get; set; } = "";
}

public class ClusterPairingAck
{
    public ClusterMember Self { get; set; } = new();
    public List<ClusterMember> Roster { get; set; } = new();
}

public class ClusterPairingResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public ClusterMember? Remote { get; set; }
    public List<ClusterMember> Roster { get; set; } = new();
}

public class ClusterJoinReport
{
    public int TargetsContacted { get; set; }
    public int TargetsPaired { get; set; }
    public List<string> AddedDeviceIds { get; set; } = new();
    public List<string> FailedTargets { get; set; } = new();
    public bool TargetLimitReached { get; set; }
}
