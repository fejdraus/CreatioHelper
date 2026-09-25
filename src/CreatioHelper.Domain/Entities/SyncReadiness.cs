namespace CreatioHelper.Domain.Entities;

public enum SyncReadiness
{
    NotConfigured,
    Offline,
    Paused,
    NotSharing,
    Unknown,
    Syncing,
    UpToDate
}

public readonly record struct SyncStatusSnapshot(SyncReadiness Readiness, double Completion)
{
    public static SyncStatusSnapshot NotConfigured { get; } = new(SyncReadiness.NotConfigured, 0);

    public bool CanEverComplete =>
        Readiness == SyncReadiness.Syncing || Readiness == SyncReadiness.UpToDate;
}
