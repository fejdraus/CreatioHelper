using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// Factory for creating versioning implementations
/// Compatible with Syncthing's versioner factory pattern
/// </summary>
public class VersionerFactory : IVersionerFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VersionerFactory> _logger;

    public VersionerFactory(IServiceProvider serviceProvider, ILogger<VersionerFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates a versioner instance based on configuration
    /// </summary>
    public IVersioner CreateVersioner(string folderPath, VersioningConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path cannot be null or empty", nameof(folderPath));
        }

        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (!config.IsEnabled)
        {
            throw new ArgumentException("Versioning is not enabled in configuration");
        }

        return config.Type.ToLowerInvariant() switch
        {
            "simple" => new SimpleVersioner(
                _serviceProvider.GetRequiredService<ILogger<SimpleVersioner>>(),
                folderPath, 
                config),
            
            "staggered" => new StaggeredVersioner(
                _serviceProvider.GetRequiredService<ILogger<StaggeredVersioner>>(),
                folderPath, 
                config),
            
            "trashcan" => new TrashcanVersioner(
                _serviceProvider.GetRequiredService<ILogger<TrashcanVersioner>>(),
                folderPath, 
                config),
            
            "external" => new ExternalVersioner(
                _serviceProvider.GetRequiredService<ILogger<ExternalVersioner>>(),
                folderPath, 
                config),
            
            _ => throw new ArgumentException($"Unknown versioning type: {config.Type}")
        };
    }

    /// <summary>
    /// Gets the available versioning types
    /// </summary>
    public static IReadOnlyList<string> AvailableTypes => new[]
    {
        "simple",
        "staggered", 
        "trashcan",
        "external"
    };

    /// <summary>
    /// Validates a versioning configuration
    /// </summary>
    public bool ValidateConfiguration(VersioningConfiguration config)
    {
        if (!config.IsEnabled)
        {
            return true; // Disabled is always valid
        }

        if (string.IsNullOrWhiteSpace(config.Type))
        {
            return false;
        }

        if (!AvailableTypes.Contains(config.Type.ToLowerInvariant()))
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
            _ => true
        };
    }

    /// <summary>
    /// Gets supported versioning types
    /// </summary>
    public IEnumerable<string> GetSupportedTypes()
    {
        return AvailableTypes;
    }

    private bool ValidateSimpleConfig(VersioningConfiguration config)
    {
        if (config.Params.TryGetValue("keep", out var keepStr))
        {
            if (!int.TryParse(keepStr, out var keep) || keep < 0)
            {
                return false;
            }
        }

        if (config.Params.TryGetValue("cleanoutDays", out var cleanoutStr))
        {
            if (!int.TryParse(cleanoutStr, out var cleanout) || cleanout < 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateStaggeredConfig(VersioningConfiguration config)
    {
        if (config.Params.TryGetValue("maxAge", out var maxAgeStr))
        {
            if (!int.TryParse(maxAgeStr, out var maxAge) || maxAge <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateTrashcanConfig(VersioningConfiguration config)
    {
        if (config.Params.TryGetValue("cleanoutDays", out var cleanoutStr))
        {
            if (!int.TryParse(cleanoutStr, out var cleanout) || cleanout < 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateExternalConfig(VersioningConfiguration config)
    {
        if (!config.Params.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return true;
    }
}