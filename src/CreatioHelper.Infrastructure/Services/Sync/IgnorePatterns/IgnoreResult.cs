using System;

namespace CreatioHelper.Infrastructure.Services.Sync.IgnorePatterns;

/// <summary>
/// Represents the result of pattern matching, compatible with Syncthing's ignoreresult.R
/// Uses bit flags for different ignore behaviors
/// </summary>
[Flags]
public enum IgnoreResult : byte
{
    /// <summary>
    /// File is not ignored
    /// </summary>
    NotIgnored = 0,
    
    /// <summary>
    /// File is ignored
    /// </summary>
    Ignored = 1,
    
    /// <summary>
    /// File can be deleted if it's blocking directory removal
    /// </summary>
    Deletable = 2,
    
    /// <summary>
    /// Entire directory can be skipped during traversal
    /// </summary>
    CanSkipDir = 4,
    
    /// <summary>
    /// Case folding should be applied (Windows/macOS behavior)
    /// </summary>
    FoldCase = 8,
    
    /// <summary>
    /// File is ignored and deletable
    /// </summary>
    IgnoredDeletable = Ignored | Deletable,
    
    /// <summary>
    /// File is ignored and directory can be skipped
    /// </summary>
    IgnoreAndSkip = Ignored | CanSkipDir
}

/// <summary>
/// Extension methods for IgnoreResult to match Syncthing's API
/// </summary>
public static class IgnoreResultExtensions
{
    /// <summary>
    /// Returns true if the file should be ignored
    /// </summary>
    public static bool IsIgnored(this IgnoreResult result)
        => (result & IgnoreResult.Ignored) != 0;
    
    /// <summary>
    /// Returns true if the file can be deleted when blocking directory removal
    /// </summary>
    public static bool IsDeletable(this IgnoreResult result)
        => (result & IgnoreResult.Deletable) != 0;
    
    /// <summary>
    /// Returns true if the entire directory can be skipped during traversal
    /// </summary>
    public static bool CanSkipDir(this IgnoreResult result)
        => (result & IgnoreResult.CanSkipDir) != 0;
    
    /// <summary>
    /// Returns true if case folding should be applied
    /// </summary>
    public static bool ShouldFoldCase(this IgnoreResult result)
        => (result & IgnoreResult.FoldCase) != 0;
    
    /// <summary>
    /// Toggles the ignored state (for negation patterns starting with !)
    /// </summary>
    public static IgnoreResult ToggleIgnored(this IgnoreResult result)
        => result ^ IgnoreResult.Ignored;
    
    /// <summary>
    /// Adds the deletable flag
    /// </summary>
    public static IgnoreResult WithDeletable(this IgnoreResult result)
        => result | IgnoreResult.Deletable;
    
    /// <summary>
    /// Adds the can skip directory flag
    /// </summary>
    public static IgnoreResult WithCanSkipDir(this IgnoreResult result)
        => result | IgnoreResult.CanSkipDir;
    
    /// <summary>
    /// Adds the case folding flag
    /// </summary>
    public static IgnoreResult WithFoldCase(this IgnoreResult result)
        => result | IgnoreResult.FoldCase;
}