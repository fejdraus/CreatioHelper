namespace CreatioHelper.Domain.Enums;

/// <summary>
/// Defines the order in which blocks are requested during file download.
/// Based on Syncthing's blockPullOrder configuration.
/// </summary>
public enum SyncBlockPullOrder
{
    /// <summary>
    /// Standard device-aware chunking for optimal parallel downloads.
    /// Requests blocks from different devices to maximize throughput.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// Shuffle blocks randomly before downloading.
    /// Useful for load balancing across multiple devices.
    /// </summary>
    Random = 1,

    /// <summary>
    /// Download blocks sequentially by offset.
    /// Useful when reading files while downloading (e.g., video streaming).
    /// </summary>
    InOrder = 2
}
