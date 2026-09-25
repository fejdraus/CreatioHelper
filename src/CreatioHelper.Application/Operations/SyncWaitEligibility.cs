using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Operations;

public enum SyncWaitExclusion
{
    None,
    NotConfigured,
    SyncPaused,
    SyncOffline,
    SyncNotSharing,
    SyncStateUnknown,
    SiteStopped,
    PoolStopped
}

public readonly record struct ServerStartState(
    SyncReadiness SyncReadiness,
    string? PoolStatus,
    string? SiteStatus);

public static class SyncWaitEligibility
{
    public static SyncWaitExclusion Decide(ServerStartState state) => state.SyncReadiness switch
    {
        SyncReadiness.NotConfigured => SyncWaitExclusion.NotConfigured,
        SyncReadiness.Paused => SyncWaitExclusion.SyncPaused,
        SyncReadiness.Offline => SyncWaitExclusion.SyncOffline,
        SyncReadiness.NotSharing => SyncWaitExclusion.SyncNotSharing,
        SyncReadiness.Unknown => SyncWaitExclusion.SyncStateUnknown,
        _ when IsStopped(state.PoolStatus) => SyncWaitExclusion.PoolStopped,
        _ when IsStopped(state.SiteStatus) => SyncWaitExclusion.SiteStopped,
        _ => SyncWaitExclusion.None
    };

    public static string Explain(SyncWaitExclusion exclusion) => exclusion switch
    {
        SyncWaitExclusion.NotConfigured => "Syncthing is not configured for it",
        SyncWaitExclusion.SyncPaused => "its synchronisation is paused, so the wait would never end",
        SyncWaitExclusion.SyncOffline => "its Syncthing device is offline, so the wait would never end",
        SyncWaitExclusion.SyncNotSharing => "the folder is not shared with it, so the wait would never end",
        SyncWaitExclusion.SyncStateUnknown => "its synchronisation state could not be read",
        SyncWaitExclusion.PoolStopped => "its application pool was already stopped before this run",
        SyncWaitExclusion.SiteStopped => "its website was already stopped before this run",
        _ => "no reason"
    };

    private static bool IsStopped(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && status.Trim().StartsWith("Stopped", StringComparison.OrdinalIgnoreCase);
}
