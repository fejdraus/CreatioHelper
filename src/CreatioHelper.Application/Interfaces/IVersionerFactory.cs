using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Factory interface for creating versioning implementations
/// Compatible with Syncthing's versioning system architecture
/// </summary>
public interface IVersionerFactory
{
    /// <summary>
    /// Creates a versioner instance based on configuration
    /// </summary>
    /// <param name="folderPath">Path to the folder being versioned</param>
    /// <param name="config">Versioning configuration</param>
    /// <returns>Configured versioner instance</returns>
    IVersioner CreateVersioner(string folderPath, VersioningConfiguration config);
    
    /// <summary>
    /// Validates versioning configuration without creating a versioner
    /// </summary>
    /// <param name="config">Configuration to validate</param>
    /// <returns>True if configuration is valid</returns>
    bool ValidateConfiguration(VersioningConfiguration config);
    
    /// <summary>
    /// Gets supported versioning types
    /// </summary>
    /// <returns>List of supported versioning strategies</returns>
    IEnumerable<string> GetSupportedTypes();
}