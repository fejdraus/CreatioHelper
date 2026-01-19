namespace CreatioHelper.Infrastructure.Services.Configuration;

/// <summary>
/// Service for managing feature flags (A/B testing, gradual rollouts, kill switches).
/// Based on Syncthing's feature flag patterns from lib/config/config.go
///
/// Key behaviors:
/// - Enable/disable features at runtime
/// - Support for percentage rollouts
/// - Device/user-specific overrides
/// - Persistent storage of flag states
/// - Event emission on flag changes
/// </summary>
public interface IFeatureFlagsService
{
    /// <summary>
    /// Check if a feature is enabled.
    /// </summary>
    /// <param name="featureName">Feature name</param>
    /// <param name="context">Optional context for percentage-based rollouts</param>
    /// <returns>True if feature is enabled</returns>
    bool IsEnabled(string featureName, string? context = null);

    /// <summary>
    /// Check if a feature is enabled asynchronously.
    /// </summary>
    Task<bool> IsEnabledAsync(string featureName, string? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the value of a feature flag.
    /// </summary>
    /// <typeparam name="T">Value type</typeparam>
    /// <param name="featureName">Feature name</param>
    /// <param name="defaultValue">Default value if not set</param>
    /// <returns>Feature value</returns>
    T GetValue<T>(string featureName, T defaultValue);

    /// <summary>
    /// Set a feature flag state.
    /// </summary>
    /// <param name="featureName">Feature name</param>
    /// <param name="enabled">Enabled state</param>
    Task SetEnabledAsync(string featureName, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Set a feature flag value.
    /// </summary>
    /// <typeparam name="T">Value type</typeparam>
    /// <param name="featureName">Feature name</param>
    /// <param name="value">Value to set</param>
    Task SetValueAsync<T>(string featureName, T value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Set rollout percentage for a feature.
    /// </summary>
    /// <param name="featureName">Feature name</param>
    /// <param name="percentage">Percentage (0-100)</param>
    Task SetRolloutPercentageAsync(string featureName, int percentage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a device-specific override.
    /// </summary>
    /// <param name="featureName">Feature name</param>
    /// <param name="deviceId">Device ID</param>
    /// <param name="enabled">Override state</param>
    Task AddDeviceOverrideAsync(string featureName, string deviceId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a device-specific override.
    /// </summary>
    /// <param name="featureName">Feature name</param>
    /// <param name="deviceId">Device ID</param>
    Task RemoveDeviceOverrideAsync(string featureName, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all feature flags.
    /// </summary>
    Task<IEnumerable<FeatureFlag>> GetAllFlagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a specific feature flag.
    /// </summary>
    Task<FeatureFlag?> GetFlagAsync(string featureName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a feature flag.
    /// </summary>
    Task DeleteFlagAsync(string featureName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a known feature with default configuration.
    /// </summary>
    void RegisterFeature(string featureName, FeatureFlagDefinition definition);

    /// <summary>
    /// Get all registered feature definitions.
    /// </summary>
    IEnumerable<FeatureFlagDefinition> GetRegisteredFeatures();

    /// <summary>
    /// Get service statistics.
    /// </summary>
    FeatureFlagsStatistics GetStatistics();

    /// <summary>
    /// Subscribe to flag change events.
    /// </summary>
    event EventHandler<FeatureFlagChangedEventArgs>? FlagChanged;
}

/// <summary>
/// Represents a feature flag.
/// </summary>
public class FeatureFlag
{
    /// <summary>
    /// Feature name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Feature description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Whether the feature is enabled globally.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Rollout percentage (0-100). 100 means fully enabled, 0 means disabled.
    /// </summary>
    public int RolloutPercentage { get; set; } = 100;

    /// <summary>
    /// Custom value for the feature (for non-boolean flags).
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Device-specific overrides.
    /// </summary>
    public Dictionary<string, bool> DeviceOverrides { get; set; } = new();

    /// <summary>
    /// When the flag was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the flag was last modified.
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Flag category for organization.
    /// </summary>
    public string Category { get; set; } = "general";

    /// <summary>
    /// Tags for filtering.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Whether this is a kill switch (can disable in emergency).
    /// </summary>
    public bool IsKillSwitch { get; set; }

    /// <summary>
    /// Start date for time-based rollout.
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// End date for time-based rollout.
    /// </summary>
    public DateTime? EndDate { get; set; }
}

/// <summary>
/// Definition for a registered feature.
/// </summary>
public class FeatureFlagDefinition
{
    /// <summary>
    /// Feature name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of the feature.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Default enabled state.
    /// </summary>
    public bool DefaultEnabled { get; set; }

    /// <summary>
    /// Default value for non-boolean flags.
    /// </summary>
    public object? DefaultValue { get; set; }

    /// <summary>
    /// Category for organization.
    /// </summary>
    public string Category { get; set; } = "general";

    /// <summary>
    /// Whether this can be used as a kill switch.
    /// </summary>
    public bool CanBeKillSwitch { get; set; }

    /// <summary>
    /// Minimum version that supports this feature.
    /// </summary>
    public string? MinVersion { get; set; }
}

/// <summary>
/// Event args for feature flag changes.
/// </summary>
public class FeatureFlagChangedEventArgs : EventArgs
{
    /// <summary>
    /// Feature name.
    /// </summary>
    public string FeatureName { get; set; } = string.Empty;

    /// <summary>
    /// Previous enabled state.
    /// </summary>
    public bool PreviousEnabled { get; set; }

    /// <summary>
    /// New enabled state.
    /// </summary>
    public bool NewEnabled { get; set; }

    /// <summary>
    /// Previous value.
    /// </summary>
    public object? PreviousValue { get; set; }

    /// <summary>
    /// New value.
    /// </summary>
    public object? NewValue { get; set; }

    /// <summary>
    /// Change type.
    /// </summary>
    public FeatureFlagChangeType ChangeType { get; set; }

    /// <summary>
    /// When the change occurred.
    /// </summary>
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Types of feature flag changes.
/// </summary>
public enum FeatureFlagChangeType
{
    Created,
    EnabledChanged,
    ValueChanged,
    RolloutChanged,
    OverrideAdded,
    OverrideRemoved,
    Deleted
}

/// <summary>
/// Statistics for feature flags service.
/// </summary>
public class FeatureFlagsStatistics
{
    /// <summary>
    /// Total number of flags.
    /// </summary>
    public int TotalFlags { get; set; }

    /// <summary>
    /// Number of enabled flags.
    /// </summary>
    public int EnabledFlags { get; set; }

    /// <summary>
    /// Number of disabled flags.
    /// </summary>
    public int DisabledFlags { get; set; }

    /// <summary>
    /// Number of flags with rollout percentage.
    /// </summary>
    public int PartialRolloutFlags { get; set; }

    /// <summary>
    /// Total evaluation count.
    /// </summary>
    public long TotalEvaluations { get; set; }

    /// <summary>
    /// Cache hit rate.
    /// </summary>
    public double CacheHitRate { get; set; }

    /// <summary>
    /// Flags by category.
    /// </summary>
    public Dictionary<string, int> FlagsByCategory { get; set; } = new();
}
