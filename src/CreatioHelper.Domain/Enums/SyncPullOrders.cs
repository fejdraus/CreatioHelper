namespace CreatioHelper.Domain.Enums;

public static class SyncPullOrders
{
    public static SyncPullOrder Parse(string? order) => order?.ToLowerInvariant() switch
    {
        "alphabetic" => SyncPullOrder.Alphabetic,
        "smallestfirst" => SyncPullOrder.SmallestFirst,
        "largestfirst" => SyncPullOrder.LargestFirst,
        "oldestfirst" => SyncPullOrder.OldestFirst,
        "newestfirst" => SyncPullOrder.NewestFirst,
        _ => SyncPullOrder.Random
    };
}
