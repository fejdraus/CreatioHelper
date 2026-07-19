using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Outcome of a conflict resolution decision produced by <see cref="ConflictResolutionEngine"/>
/// and applied by the folder handlers.
/// </summary>
public class ConflictResolution
{
    public ConflictAction Action { get; set; } = ConflictAction.NoAction;
    public string Reason { get; set; } = string.Empty;
    public string? ConflictCopyName { get; set; }
    public string? LocalCopyName { get; set; }

    /// <summary>
    /// Winning version of the file (when applicable).
    /// </summary>
    public FileMetadata? Winner { get; set; }

    /// <summary>
    /// Name of the conflict copy file (when a copy is created).
    /// </summary>
    public string? ConflictFileName { get; set; }
}

public enum ConflictAction
{
    NoAction,
    KeepLocal,
    AcceptRemote,
    CreateConflictCopy,
    CreateBothCopies,
    UseLocal,
    UseRemote,
    Override,
    Revert,
    Skip
}
