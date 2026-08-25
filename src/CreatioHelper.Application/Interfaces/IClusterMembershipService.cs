namespace CreatioHelper.Application.Interfaces;

public interface IClusterMembershipService
{
    bool IsEnabled { get; }

    ClusterMember GetLocalMember();

    Task AcceptPairedDeviceAsync(ClusterMember remote, CancellationToken cancellationToken = default);

    string? ResolveApiAddress(string deviceId, IEnumerable<string>? addresses = null);
}

public class ClusterMember
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    public string ApiAddress { get; set; } = "";
    public DateTime AdmittedAt { get; set; }
    public long StateVersion { get; set; }
}
