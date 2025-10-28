namespace CreatioHelper.Domain.Enums;

/// <summary>
/// Type of synchronization for a server
/// </summary>
public enum ServerSyncType
{
    /// <summary>
    /// No synchronization configured
    /// </summary>
    None = 0,

    /// <summary>
    /// Use Syncthing for synchronization
    /// </summary>
    Syncthing = 1,

    /// <summary>
    /// Use file copy for synchronization
    /// </summary>
    FileCopy = 2
}
