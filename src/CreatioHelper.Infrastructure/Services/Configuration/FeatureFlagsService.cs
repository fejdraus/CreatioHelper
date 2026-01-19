using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Configuration;

/// <summary>
/// Implementation of feature flags service.
/// Supports:
/// - Boolean and value-based flags
/// - Percentage-based rollouts
/// - Device-specific overrides
/// - Persistent storage to JSON file
/// - Event emission on changes
/// </summary>
public class FeatureFlagsService : IFeatureFlagsService, IDisposable
{
    private readonly ILogger<FeatureFlagsService> _logger;
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, FeatureFlag> _flags = new();
    private readonly ConcurrentDictionary<string, FeatureFlagDefinition> _registeredFeatures = new();
    private readonly SemaphoreSlim _saveLock = new(1);
    private readonly object _statsLock = new();

    private long _totalEvaluations;
    private long _cacheHits;
    private bool _disposed;

    public event EventHandler<FeatureFlagChangedEventArgs>? FlagChanged;

    public FeatureFlagsService(
        ILogger<FeatureFlagsService> logger,
        string? storagePath = null)
    {
        _logger = logger;
        _storagePath = storagePath ?? GetDefaultStoragePath();

        // Load existing flags from storage
        _ = LoadFlagsAsync();

        // Register built-in Syncthing-compatible features
        RegisterBuiltInFeatures();

        _logger.LogInformation("Feature flags service initialized with storage at {Path}", _storagePath);
    }

    /// <inheritdoc />
    public bool IsEnabled(string featureName, string? context = null)
    {
        Interlocked.Increment(ref _totalEvaluations);

        // Check cache
        if (_flags.TryGetValue(featureName, out var flag))
        {
            Interlocked.Increment(ref _cacheHits);
            return EvaluateFlag(flag, context);
        }

        // Check registered features for default
        if (_registeredFeatures.TryGetValue(featureName, out var definition))
        {
            return definition.DefaultEnabled;
        }

        // Unknown feature - default to disabled
        _logger.LogDebug("Unknown feature flag {FeatureName}, returning disabled", featureName);
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(string featureName, string? context = null, CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(IsEnabled(featureName, context));
    }

    /// <inheritdoc />
    public T GetValue<T>(string featureName, T defaultValue)
    {
        if (_flags.TryGetValue(featureName, out var flag) && flag.Value != null)
        {
            try
            {
                if (flag.Value is T typedValue)
                {
                    return typedValue;
                }

                if (flag.Value is JsonElement jsonElement)
                {
                    return JsonSerializer.Deserialize<T>(jsonElement.GetRawText()) ?? defaultValue;
                }

                return (T)Convert.ChangeType(flag.Value, typeof(T));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert feature flag {FeatureName} value to {Type}",
                    featureName, typeof(T).Name);
            }
        }

        return defaultValue;
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(string featureName, bool enabled, CancellationToken cancellationToken = default)
    {
        var flag = _flags.GetOrAdd(featureName, _ => new FeatureFlag { Name = featureName });
        var previousEnabled = flag.Enabled;

        flag.Enabled = enabled;
        flag.ModifiedAt = DateTime.UtcNow;

        await SaveFlagsAsync(cancellationToken);

        if (previousEnabled != enabled)
        {
            OnFlagChanged(new FeatureFlagChangedEventArgs
            {
                FeatureName = featureName,
                PreviousEnabled = previousEnabled,
                NewEnabled = enabled,
                ChangeType = FeatureFlagChangeType.EnabledChanged
            });

            _logger.LogInformation("Feature flag {FeatureName} enabled changed from {Previous} to {New}",
                featureName, previousEnabled, enabled);
        }
    }

    /// <inheritdoc />
    public async Task SetValueAsync<T>(string featureName, T value, CancellationToken cancellationToken = default)
    {
        var flag = _flags.GetOrAdd(featureName, _ => new FeatureFlag { Name = featureName });
        var previousValue = flag.Value;

        flag.Value = value;
        flag.ModifiedAt = DateTime.UtcNow;

        await SaveFlagsAsync(cancellationToken);

        OnFlagChanged(new FeatureFlagChangedEventArgs
        {
            FeatureName = featureName,
            PreviousValue = previousValue,
            NewValue = value,
            ChangeType = FeatureFlagChangeType.ValueChanged
        });

        _logger.LogInformation("Feature flag {FeatureName} value changed", featureName);
    }

    /// <inheritdoc />
    public async Task SetRolloutPercentageAsync(string featureName, int percentage, CancellationToken cancellationToken = default)
    {
        percentage = Math.Clamp(percentage, 0, 100);

        var flag = _flags.GetOrAdd(featureName, _ => new FeatureFlag { Name = featureName });
        var previousPercentage = flag.RolloutPercentage;

        flag.RolloutPercentage = percentage;
        flag.ModifiedAt = DateTime.UtcNow;

        await SaveFlagsAsync(cancellationToken);

        if (previousPercentage != percentage)
        {
            OnFlagChanged(new FeatureFlagChangedEventArgs
            {
                FeatureName = featureName,
                PreviousValue = previousPercentage,
                NewValue = percentage,
                ChangeType = FeatureFlagChangeType.RolloutChanged
            });

            _logger.LogInformation("Feature flag {FeatureName} rollout changed from {Previous}% to {New}%",
                featureName, previousPercentage, percentage);
        }
    }

    /// <inheritdoc />
    public async Task AddDeviceOverrideAsync(string featureName, string deviceId, bool enabled, CancellationToken cancellationToken = default)
    {
        var flag = _flags.GetOrAdd(featureName, _ => new FeatureFlag { Name = featureName });

        flag.DeviceOverrides[deviceId] = enabled;
        flag.ModifiedAt = DateTime.UtcNow;

        await SaveFlagsAsync(cancellationToken);

        OnFlagChanged(new FeatureFlagChangedEventArgs
        {
            FeatureName = featureName,
            NewValue = new { DeviceId = deviceId, Enabled = enabled },
            ChangeType = FeatureFlagChangeType.OverrideAdded
        });

        _logger.LogInformation("Added device override for {FeatureName}: {DeviceId} = {Enabled}",
            featureName, deviceId, enabled);
    }

    /// <inheritdoc />
    public async Task RemoveDeviceOverrideAsync(string featureName, string deviceId, CancellationToken cancellationToken = default)
    {
        if (_flags.TryGetValue(featureName, out var flag))
        {
            if (flag.DeviceOverrides.Remove(deviceId))
            {
                flag.ModifiedAt = DateTime.UtcNow;

                await SaveFlagsAsync(cancellationToken);

                OnFlagChanged(new FeatureFlagChangedEventArgs
                {
                    FeatureName = featureName,
                    PreviousValue = deviceId,
                    ChangeType = FeatureFlagChangeType.OverrideRemoved
                });

                _logger.LogInformation("Removed device override for {FeatureName}: {DeviceId}",
                    featureName, deviceId);
            }
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<FeatureFlag>> GetAllFlagsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<FeatureFlag>>(_flags.Values.ToList());
    }

    /// <inheritdoc />
    public Task<FeatureFlag?> GetFlagAsync(string featureName, CancellationToken cancellationToken = default)
    {
        _flags.TryGetValue(featureName, out var flag);
        return Task.FromResult(flag);
    }

    /// <inheritdoc />
    public async Task DeleteFlagAsync(string featureName, CancellationToken cancellationToken = default)
    {
        if (_flags.TryRemove(featureName, out var flag))
        {
            await SaveFlagsAsync(cancellationToken);

            OnFlagChanged(new FeatureFlagChangedEventArgs
            {
                FeatureName = featureName,
                PreviousEnabled = flag.Enabled,
                ChangeType = FeatureFlagChangeType.Deleted
            });

            _logger.LogInformation("Deleted feature flag {FeatureName}", featureName);
        }
    }

    /// <inheritdoc />
    public void RegisterFeature(string featureName, FeatureFlagDefinition definition)
    {
        definition.Name = featureName;
        _registeredFeatures[featureName] = definition;

        _logger.LogDebug("Registered feature {FeatureName}: {Description}",
            featureName, definition.Description);
    }

    /// <inheritdoc />
    public IEnumerable<FeatureFlagDefinition> GetRegisteredFeatures()
    {
        return _registeredFeatures.Values.ToList();
    }

    /// <inheritdoc />
    public FeatureFlagsStatistics GetStatistics()
    {
        var flags = _flags.Values.ToList();

        return new FeatureFlagsStatistics
        {
            TotalFlags = flags.Count,
            EnabledFlags = flags.Count(f => f.Enabled),
            DisabledFlags = flags.Count(f => !f.Enabled),
            PartialRolloutFlags = flags.Count(f => f.RolloutPercentage > 0 && f.RolloutPercentage < 100),
            TotalEvaluations = Interlocked.Read(ref _totalEvaluations),
            CacheHitRate = _totalEvaluations > 0 ? (double)_cacheHits / _totalEvaluations * 100 : 0,
            FlagsByCategory = flags.GroupBy(f => f.Category)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    /// <summary>
    /// Evaluate a feature flag considering rollout percentage and device overrides.
    /// </summary>
    private bool EvaluateFlag(FeatureFlag flag, string? context)
    {
        // Check if flag is within date range
        if (flag.StartDate.HasValue && DateTime.UtcNow < flag.StartDate.Value)
        {
            return false;
        }

        if (flag.EndDate.HasValue && DateTime.UtcNow > flag.EndDate.Value)
        {
            return false;
        }

        // Check for device override
        if (!string.IsNullOrEmpty(context) && flag.DeviceOverrides.TryGetValue(context, out var deviceOverride))
        {
            return deviceOverride;
        }

        // Check base enabled state
        if (!flag.Enabled)
        {
            return false;
        }

        // Check rollout percentage
        if (flag.RolloutPercentage < 100)
        {
            return IsInRollout(flag.Name, context, flag.RolloutPercentage);
        }

        return true;
    }

    /// <summary>
    /// Determine if context is in rollout percentage using consistent hashing.
    /// </summary>
    private static bool IsInRollout(string featureName, string? context, int percentage)
    {
        // Use consistent hashing to ensure same context always gets same result
        var hashInput = $"{featureName}:{context ?? "default"}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));

        // Use first 4 bytes as an integer
        var hashValue = BitConverter.ToUInt32(hashBytes, 0);
        var normalizedValue = hashValue % 100;

        return normalizedValue < percentage;
    }

    /// <summary>
    /// Load flags from storage.
    /// </summary>
    private async Task LoadFlagsAsync()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = await File.ReadAllTextAsync(_storagePath);
                var flags = JsonSerializer.Deserialize<List<FeatureFlag>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (flags != null)
                {
                    foreach (var flag in flags)
                    {
                        _flags[flag.Name] = flag;
                    }

                    _logger.LogInformation("Loaded {Count} feature flags from storage", flags.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load feature flags from {Path}", _storagePath);
        }
    }

    /// <summary>
    /// Save flags to storage.
    /// </summary>
    private async Task SaveFlagsAsync(CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_flags.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(_storagePath, json, cancellationToken);

            _logger.LogDebug("Saved {Count} feature flags to storage", _flags.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save feature flags to {Path}", _storagePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Register built-in Syncthing-compatible features.
    /// </summary>
    private void RegisterBuiltInFeatures()
    {
        // Network features
        RegisterFeature("local-discovery", new FeatureFlagDefinition
        {
            Description = "Enable local device discovery via LAN broadcast",
            DefaultEnabled = true,
            Category = "network"
        });

        RegisterFeature("global-discovery", new FeatureFlagDefinition
        {
            Description = "Enable global device discovery via discovery servers",
            DefaultEnabled = true,
            Category = "network"
        });

        RegisterFeature("nat-traversal", new FeatureFlagDefinition
        {
            Description = "Enable NAT traversal (UPnP, NAT-PMP, STUN)",
            DefaultEnabled = true,
            Category = "network"
        });

        RegisterFeature("relay-connections", new FeatureFlagDefinition
        {
            Description = "Allow connections via relay servers",
            DefaultEnabled = true,
            Category = "network"
        });

        RegisterFeature("quic-transport", new FeatureFlagDefinition
        {
            Description = "Enable QUIC transport protocol",
            DefaultEnabled = false,
            Category = "network"
        });

        // Sync features
        RegisterFeature("delta-sync", new FeatureFlagDefinition
        {
            Description = "Enable delta synchronization for block-level changes",
            DefaultEnabled = true,
            Category = "sync"
        });

        RegisterFeature("compression", new FeatureFlagDefinition
        {
            Description = "Enable data compression during transfer",
            DefaultEnabled = true,
            Category = "sync"
        });

        RegisterFeature("encryption", new FeatureFlagDefinition
        {
            Description = "Enable end-to-end encryption for untrusted devices",
            DefaultEnabled = false,
            Category = "sync"
        });

        // Security features
        RegisterFeature("crash-reporting", new FeatureFlagDefinition
        {
            Description = "Enable automatic crash reporting",
            DefaultEnabled = false,
            Category = "security",
            CanBeKillSwitch = true
        });

        RegisterFeature("usage-reporting", new FeatureFlagDefinition
        {
            Description = "Enable anonymous usage statistics reporting",
            DefaultEnabled = false,
            Category = "security",
            CanBeKillSwitch = true
        });

        RegisterFeature("auto-upgrade", new FeatureFlagDefinition
        {
            Description = "Enable automatic software upgrades",
            DefaultEnabled = false,
            Category = "security",
            CanBeKillSwitch = true
        });

        // UI features
        RegisterFeature("web-gui", new FeatureFlagDefinition
        {
            Description = "Enable web-based GUI",
            DefaultEnabled = true,
            Category = "ui"
        });

        RegisterFeature("dark-mode", new FeatureFlagDefinition
        {
            Description = "Enable dark mode UI theme",
            DefaultEnabled = false,
            Category = "ui"
        });

        // Experimental features
        RegisterFeature("experimental-block-finder", new FeatureFlagDefinition
        {
            Description = "Use experimental block finding algorithm",
            DefaultEnabled = false,
            Category = "experimental"
        });

        RegisterFeature("experimental-fs-watcher", new FeatureFlagDefinition
        {
            Description = "Use experimental file system watcher",
            DefaultEnabled = false,
            Category = "experimental"
        });
    }

    /// <summary>
    /// Raise flag changed event.
    /// </summary>
    private void OnFlagChanged(FeatureFlagChangedEventArgs args)
    {
        FlagChanged?.Invoke(this, args);
    }

    /// <summary>
    /// Get default storage path.
    /// </summary>
    private static string GetDefaultStoragePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "CreatioHelper", "feature-flags.json");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _saveLock.Dispose();
            _disposed = true;
        }
    }
}
