namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

public class ClusterMembershipRegistry
{
    private readonly object _lock = new();
    private HashSet<string> _members = new(StringComparer.OrdinalIgnoreCase);

    public bool Active { get; private set; }

    public void Update(IEnumerable<string> memberDeviceIds)
    {
        var snapshot = new HashSet<string>(memberDeviceIds, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            _members = snapshot;
            Active = true;
        }
    }

    public bool IsMember(string deviceId)
    {
        lock (_lock)
        {
            return _members.Contains(deviceId);
        }
    }
}
