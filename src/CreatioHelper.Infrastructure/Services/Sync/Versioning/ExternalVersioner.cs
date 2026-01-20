using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// External versioner that delegates versioning to an external script/command.
/// Compatible with Syncthing's external versioner.
/// The command is called with: command "archive" filePath folderPath
/// </summary>
public class ExternalVersioner : IVersioner, IDisposable
{
    private readonly ILogger<ExternalVersioner> _logger;
    private readonly string _folderPath;
    private readonly string _command;
    private readonly string? _versionsPath;
    private readonly int _cleanupIntervalS;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new external versioner.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="folderPath">Path to the folder being versioned.</param>
    /// <param name="command">External command to execute for versioning.</param>
    /// <param name="versionsPath">Optional custom path for storing versions (used by restore).</param>
    /// <param name="cleanupIntervalS">Cleanup interval in seconds.</param>
    public ExternalVersioner(
        ILogger<ExternalVersioner> logger,
        string folderPath,
        string command,
        string? versionsPath = null,
        int cleanupIntervalS = 3600)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _folderPath = folderPath ?? throw new ArgumentNullException(nameof(folderPath));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _versionsPath = versionsPath ?? Path.Combine(folderPath, ".stversions");
        _cleanupIntervalS = cleanupIntervalS;

        // Ensure versions directory exists for GetVersionsAsync
        if (!Directory.Exists(_versionsPath))
        {
            Directory.CreateDirectory(_versionsPath);
        }

        _logger.LogInformation("ExternalVersioner initialized with command: {Command} for folder: {FolderPath}",
            _command, _folderPath);
    }

    /// <inheritdoc />
    public string VersionerType => "external";

    /// <inheritdoc />
    public string VersionsPath => _versionsPath ?? Path.Combine(_folderPath, ".stversions");

    /// <inheritdoc />
    public async Task ArchiveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));

        var fullFilePath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(_folderPath, filePath);

        if (!File.Exists(fullFilePath))
        {
            _logger.LogWarning("File does not exist for archiving: {FilePath}", fullFilePath);
            return;
        }

        _logger.LogInformation("Archiving file via external command: {FilePath}", fullFilePath);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _command,
                Arguments = $"archive \"{fullFilePath}\" \"{_folderPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Read output streams
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Wait for process to complete with timeout
            var completed = await Task.Run(() => process.WaitForExit(60000), cancellationToken);

            if (!completed)
            {
                process.Kill(true);
                throw new TimeoutException($"External versioner command timed out: {_command}");
            }

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                _logger.LogError("External versioner command failed with exit code {ExitCode}: {Error}",
                    process.ExitCode, error);
                throw new InvalidOperationException($"External versioner failed: {error}");
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("External versioner output: {Output}", output);
            }

            _logger.LogInformation("Successfully archived file via external command: {FilePath}", filePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error executing external versioner command for file: {FilePath}", filePath);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Dictionary<string, List<FileVersion>>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = new Dictionary<string, List<FileVersion>>();

        if (!Directory.Exists(VersionsPath))
        {
            return Task.FromResult(versions);
        }

        try
        {
            // External versioner may use any naming scheme, but we try to find versioned files
            var allFiles = Directory.GetFiles(VersionsPath, "*", SearchOption.AllDirectories);

            foreach (var versionFile in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var fileInfo = new FileInfo(versionFile);
                    var relativePath = Path.GetRelativePath(VersionsPath, versionFile);

                    // Try to parse version time from filename (common patterns)
                    var originalPath = ParseOriginalPath(relativePath);
                    var versionTime = ParseVersionTime(relativePath, fileInfo.LastWriteTimeUtc);

                    if (!versions.ContainsKey(originalPath))
                    {
                        versions[originalPath] = new List<FileVersion>();
                    }

                    versions[originalPath].Add(new FileVersion
                    {
                        VersionTime = versionTime,
                        ModTime = fileInfo.LastWriteTimeUtc,
                        Size = fileInfo.Length,
                        VersionPath = versionFile,
                        OriginalPath = originalPath
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing version file: {VersionFile}", versionFile);
                }
            }

            // Sort versions by time (newest first)
            foreach (var key in versions.Keys)
            {
                versions[key] = versions[key].OrderByDescending(v => v.VersionTime).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing versions in: {VersionsPath}", VersionsPath);
        }

        return Task.FromResult(versions);
    }

    /// <inheritdoc />
    public async Task RestoreAsync(string filePath, DateTime versionTime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));

        var versions = await GetVersionsAsync(cancellationToken);

        if (!versions.TryGetValue(filePath, out var fileVersions) || fileVersions.Count == 0)
        {
            throw new FileNotFoundException($"No versions found for file: {filePath}");
        }

        // Find the version closest to the requested time
        var targetVersion = fileVersions
            .OrderBy(v => Math.Abs((v.VersionTime - versionTime).TotalSeconds))
            .FirstOrDefault();

        if (targetVersion == null)
        {
            throw new FileNotFoundException($"Version not found for time: {versionTime}");
        }

        var destinationPath = Path.Combine(_folderPath, filePath);
        var destinationDir = Path.GetDirectoryName(destinationPath);

        if (destinationDir != null && !Directory.Exists(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        // Archive current file before restore if it exists
        if (File.Exists(destinationPath))
        {
            await ArchiveAsync(filePath, cancellationToken);
        }

        File.Copy(targetVersion.VersionPath, destinationPath, overwrite: true);

        _logger.LogInformation("Restored file {FilePath} from version {VersionTime}",
            filePath, targetVersion.VersionTime);
    }

    /// <inheritdoc />
    public Task CleanAsync(CancellationToken cancellationToken = default)
    {
        // External versioner is responsible for its own cleanup
        // We just log that cleanup was requested
        _logger.LogDebug("Cleanup requested for external versioner - delegating to external command");

        // Optionally call external command with "clean" argument
        // This is an extension to the standard Syncthing behavior
        return Task.CompletedTask;
    }

    /// <summary>
    /// Parses the original file path from a versioned filename.
    /// Handles common patterns like: file~20240115-103000.txt or 20240115-103000~file.txt
    /// </summary>
    private static string ParseOriginalPath(string versionPath)
    {
        // Try to find Syncthing-style timestamp pattern: ~YYYYMMDD-HHMMSS
        var pattern = @"~\d{8}-\d{6}";
        var match = System.Text.RegularExpressions.Regex.Match(versionPath, pattern);

        if (match.Success)
        {
            // Remove the timestamp portion
            return versionPath.Replace(match.Value, "");
        }

        // If no timestamp found, use the filename as-is
        return versionPath;
    }

    /// <summary>
    /// Parses the version time from a versioned filename.
    /// </summary>
    private static DateTime ParseVersionTime(string versionPath, DateTime fallback)
    {
        // Try to extract Syncthing-style timestamp: ~YYYYMMDD-HHMMSS
        var pattern = @"~(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})";
        var match = System.Text.RegularExpressions.Regex.Match(versionPath, pattern);

        if (match.Success && match.Groups.Count >= 7)
        {
            try
            {
                return new DateTime(
                    int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value),
                    int.Parse(match.Groups[3].Value),
                    int.Parse(match.Groups[4].Value),
                    int.Parse(match.Groups[5].Value),
                    int.Parse(match.Groups[6].Value),
                    DateTimeKind.Utc);
            }
            catch
            {
                // Parsing failed, use fallback
            }
        }

        return fallback;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }
            _disposed = true;
        }
    }
}
