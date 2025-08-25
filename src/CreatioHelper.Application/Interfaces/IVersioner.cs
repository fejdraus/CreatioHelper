using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// File versioning interface compatible with Syncthing's versioning system
/// Supports Simple, Staggered, Trashcan, and External versioning strategies
/// </summary>
public interface IVersioner : IDisposable
{
    /// <summary>
    /// Archives a file before it's overwritten or deleted
    /// Creates a timestamped copy in the versions directory
    /// </summary>
    /// <param name="filePath">Path to the file to archive</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the archive operation</returns>
    Task ArchiveAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available versions for files in the folder
    /// Returns a dictionary mapping file paths to their version lists
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of file paths to version lists</returns>
    Task<Dictionary<string, List<FileVersion>>> GetVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a specific version of a file
    /// Copies the versioned file back to its original location
    /// </summary>
    /// <param name="filePath">Path to the file to restore</param>
    /// <param name="versionTime">Timestamp of the version to restore</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the restore operation</returns>
    Task RestoreAsync(string filePath, DateTime versionTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs cleanup of old versions based on retention policy
    /// Called periodically to remove expired or excess versions
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the cleanup operation</returns>
    Task CleanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the versioning strategy type (Simple, Staggered, Trashcan, External)
    /// </summary>
    string VersionerType { get; }

    /// <summary>
    /// Gets the folder path where versions are stored
    /// Default: .stversions within the synced folder
    /// </summary>
    string VersionsPath { get; }
}