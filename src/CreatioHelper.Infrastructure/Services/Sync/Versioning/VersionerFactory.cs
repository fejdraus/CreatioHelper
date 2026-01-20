using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// Factory for creating versioner instances based on configuration.
/// Supports Simple, Staggered, Trashcan, and External versioning strategies.
/// Compatible with Syncthing's versioning system.
/// </summary>
public class VersionerFactory : IVersionerFactory
{
    private readonly ILoggerFactory _loggerFactory;

    private static readonly HashSet<string> SupportedVersionerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "simple",
        "staggered",
        "trashcan",
        "external"
    };

    public VersionerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Creates a versioner instance based on the provided configuration.
    /// </summary>
    /// <param name="folderPath">Path to the folder being versioned.</param>
    /// <param name="config">Versioning configuration.</param>
    /// <returns>Configured versioner instance.</returns>
    /// <exception cref="ArgumentException">Thrown when versioner type is not supported.</exception>
    public IVersioner CreateVersioner(string folderPath, VersioningConfiguration config)
    {
        if (config == null || !config.IsEnabled)
        {
            throw new ArgumentException("Versioning is not enabled", nameof(config));
        }

        var versionsPath = !string.IsNullOrEmpty(config.FSPath)
            ? (Path.IsPathRooted(config.FSPath) ? config.FSPath : Path.Combine(folderPath, config.FSPath))
            : null;

        var cleanupIntervalS = config.CleanupIntervalS > 0 ? config.CleanupIntervalS : 3600;

        return config.Type.ToLowerInvariant() switch
        {
            "simple" => CreateSimpleVersioner(folderPath, config, versionsPath, cleanupIntervalS),
            "staggered" => CreateStaggeredVersioner(folderPath, config, versionsPath, cleanupIntervalS),
            "trashcan" => CreateTrashcanVersioner(folderPath, config, versionsPath, cleanupIntervalS),
            "external" => CreateExternalVersioner(folderPath, config, versionsPath, cleanupIntervalS),
            _ => throw new ArgumentException($"Unsupported versioner type: {config.Type}", nameof(config))
        };
    }

    /// <summary>
    /// Validates versioning configuration without creating a versioner.
    /// </summary>
    public bool ValidateConfiguration(VersioningConfiguration config)
    {
        if (config == null || !config.IsEnabled)
        {
            return true; // Disabled is valid
        }

        if (!SupportedVersionerTypes.Contains(config.Type))
        {
            return false;
        }

        // Validate type-specific parameters
        return config.Type.ToLowerInvariant() switch
        {
            "simple" => ValidateSimpleConfig(config),
            "staggered" => ValidateStaggeredConfig(config),
            "trashcan" => ValidateTrashcanConfig(config),
            "external" => ValidateExternalConfig(config),
            _ => false
        };
    }

    /// <summary>
    /// Gets all supported versioning types.
    /// </summary>
    public IEnumerable<string> GetSupportedTypes()
    {
        return SupportedVersionerTypes;
    }

    private SimpleVersioner CreateSimpleVersioner(string folderPath, VersioningConfiguration config, string? versionsPath, int cleanupIntervalS)
    {
        var keep = GetIntParam(config.Params, "keep", 5);
        var cleanoutDays = GetIntParam(config.Params, "cleanoutDays", 0);

        var logger = _loggerFactory.CreateLogger<SimpleVersioner>();
        return new SimpleVersioner(logger, folderPath, keep, cleanoutDays, versionsPath, cleanupIntervalS);
    }

    private StaggeredVersioner CreateStaggeredVersioner(string folderPath, VersioningConfiguration config, string? versionsPath, int cleanupIntervalS)
    {
        var maxAgeSeconds = GetIntParam(config.Params, "maxAge", 365 * 24 * 3600); // Default: 1 year

        var logger = _loggerFactory.CreateLogger<StaggeredVersioner>();
        return new StaggeredVersioner(logger, folderPath, maxAgeSeconds, versionsPath, cleanupIntervalS);
    }

    private TrashcanVersioner CreateTrashcanVersioner(string folderPath, VersioningConfiguration config, string? versionsPath, int cleanupIntervalS)
    {
        var cleanoutDays = GetIntParam(config.Params, "cleanoutDays", 0);

        var logger = _loggerFactory.CreateLogger<TrashcanVersioner>();
        return new TrashcanVersioner(logger, folderPath, cleanoutDays, versionsPath, cleanupIntervalS);
    }

    private static bool ValidateSimpleConfig(VersioningConfiguration config)
    {
        var keep = GetIntParam(config.Params, "keep", 5);
        var cleanoutDays = GetIntParam(config.Params, "cleanoutDays", 0);
        return keep >= 1 && cleanoutDays >= 0;
    }

    private static bool ValidateStaggeredConfig(VersioningConfiguration config)
    {
        var maxAge = GetIntParam(config.Params, "maxAge", 365 * 24 * 3600);
        return maxAge >= 0;
    }

    private static bool ValidateTrashcanConfig(VersioningConfiguration config)
    {
        var cleanoutDays = GetIntParam(config.Params, "cleanoutDays", 0);
        return cleanoutDays >= 0;
    }

    private ExternalVersioner CreateExternalVersioner(string folderPath, VersioningConfiguration config, string? versionsPath, int cleanupIntervalS)
    {
        var command = GetStringParam(config.Params, "command", string.Empty);
        if (string.IsNullOrEmpty(command))
        {
            throw new ArgumentException("External versioner requires 'command' parameter", nameof(config));
        }

        var logger = _loggerFactory.CreateLogger<ExternalVersioner>();
        return new ExternalVersioner(logger, folderPath, command, versionsPath, cleanupIntervalS);
    }

    private static bool ValidateExternalConfig(VersioningConfiguration config)
    {
        var command = GetStringParam(config.Params, "command", string.Empty);
        return !string.IsNullOrEmpty(command);
    }

    private static int GetIntParam(Dictionary<string, string> @params, string key, int defaultValue)
    {
        if (@params == null || !@params.TryGetValue(key, out var value))
            return defaultValue;

        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    private static string GetStringParam(Dictionary<string, string> @params, string key, string defaultValue)
    {
        if (@params == null || !@params.TryGetValue(key, out var value))
            return defaultValue;

        return value ?? defaultValue;
    }
}
