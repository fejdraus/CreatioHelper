namespace CreatioHelper.Domain.Enums;

/// <summary>
/// Defines the order in which files are downloaded during sync.
/// Based on Syncthing's pullOrder configuration.
/// </summary>
public enum SyncPullOrder
{
    /// <summary>
    /// Download files in random order (default, prevents hotspots).
    /// </summary>
    Random = 0,

    /// <summary>
    /// Download files alphabetically by name.
    /// </summary>
    Alphabetic = 1,

    /// <summary>
    /// Download smallest files first.
    /// </summary>
    SmallestFirst = 2,

    /// <summary>
    /// Download largest files first.
    /// </summary>
    LargestFirst = 3,

    /// <summary>
    /// Download oldest files first (by modification time).
    /// </summary>
    OldestFirst = 4,

    /// <summary>
    /// Download newest files first (by modification time).
    /// </summary>
    NewestFirst = 5
}
