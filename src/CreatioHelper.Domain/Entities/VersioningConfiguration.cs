namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Versioning configuration compatible with Syncthing's VersioningConfiguration
/// Defines how file versions are managed for a sync folder
/// </summary>
public class VersioningConfiguration
{
    /// <summary>
    /// Versioning strategy type: "simple", "staggered", "trashcan", "external"
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Strategy-specific parameters
    /// Simple: "keep" (number), "cleanoutDays" (number)
    /// Staggered: "maxAge" (seconds)
    /// Trashcan: "cleanoutDays" (number)
    /// External: "command" (string)
    /// </summary>
    public Dictionary<string, string> Params { get; set; } = new();

    /// <summary>
    /// Cleanup interval in seconds (default: 3600 = 1 hour)
    /// How often to run automatic cleanup of old versions
    /// </summary>
    public int CleanupIntervalS { get; set; } = 3600;

    /// <summary>
    /// Custom path for versions folder (default: ".stversions" within sync folder)
    /// Can be absolute or relative to sync folder
    /// </summary>
    public string FSPath { get; set; } = ".stversions";

    /// <summary>
    /// Filesystem type for versions folder (default: "basic")
    /// Allows using different filesystem implementations
    /// </summary>
    public string FSType { get; set; } = "basic";

    /// <summary>
    /// Creates a disabled versioning configuration
    /// </summary>
    public static VersioningConfiguration Disabled => new() { Type = "" };

    /// <summary>
    /// Creates a simple versioning configuration
    /// </summary>
    public static VersioningConfiguration Simple(int keep = 5, int cleanoutDays = 0) => new()
    {
        Type = "simple",
        Params = new Dictionary<string, string>
        {
            ["keep"] = keep.ToString(),
            ["cleanoutDays"] = cleanoutDays.ToString()
        }
    };

    /// <summary>
    /// Creates a staggered versioning configuration
    /// </summary>
    public static VersioningConfiguration Staggered(int maxAgeSeconds = 31536000) => new()
    {
        Type = "staggered", 
        Params = new Dictionary<string, string>
        {
            ["maxAge"] = maxAgeSeconds.ToString()
        }
    };

    /// <summary>
    /// Creates a trashcan versioning configuration
    /// </summary>
    public static VersioningConfiguration Trashcan(int cleanoutDays = 0) => new()
    {
        Type = "trashcan",
        Params = new Dictionary<string, string>
        {
            ["cleanoutDays"] = cleanoutDays.ToString()
        }
    };

    /// <summary>
    /// Gets whether versioning is enabled
    /// </summary>
    public bool IsEnabled => !string.IsNullOrEmpty(Type);

    public override string ToString()
    {
        if (!IsEnabled) return "Versioning: Disabled";
        var paramStr = string.Join(", ", Params.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"Versioning: {Type} ({paramStr})";
    }
}