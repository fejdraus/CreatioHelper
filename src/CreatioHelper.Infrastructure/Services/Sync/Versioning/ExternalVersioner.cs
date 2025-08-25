using System.Diagnostics;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// External versioning implementation compatible with Syncthing's external versioner
/// Delegates versioning to an external command/script
/// Template variables: %FOLDER_PATH%, %FILE_PATH%, %FOLDER_FILESYSTEM%
/// Note: Does not support restoration or version listing (as per Syncthing)
/// </summary>
public class ExternalVersioner : BaseVersioner
{
    private readonly string _command;

    public ExternalVersioner(ILogger<ExternalVersioner> logger, string folderPath, VersioningConfiguration config) 
        : base(logger, folderPath, config)
    {
        _command = config.Params.GetValueOrDefault("command", "");
        
        if (string.IsNullOrWhiteSpace(_command))
        {
            throw new ArgumentException("External versioning requires a 'command' parameter");
        }

        _logger.LogInformation("External versioner initialized: command='{Command}', versionsPath={VersionsPath}", 
            _command, VersionsPath);
    }

    public override string VersionerType => "external";

    public override async Task ArchiveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_folderPath, filePath);
        
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Cannot archive non-existent file: {FilePath}", filePath);
            return;
        }

        try
        {
            var expandedCommand = ExpandTemplateVariables(_command, filePath);
            
            _logger.LogDebug("Executing external versioning command: {Command}", expandedCommand);
            
            var result = await ExecuteCommandAsync(expandedCommand, cancellationToken).ConfigureAwait(false);
            
            if (result.Success)
            {
                _logger.LogInformation("External versioning completed for {FilePath}: {Output}", 
                    filePath, result.Output);
            }
            else
            {
                _logger.LogError("External versioning failed for {FilePath}: {Error}", 
                    filePath, result.Error);
                throw new InvalidOperationException($"External versioning command failed: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute external versioning for {FilePath}: {Error}", filePath, ex.Message);
            throw;
        }
    }

    public override Task<Dictionary<string, List<FileVersion>>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        // External versioning does not support version listing
        _logger.LogWarning("GetVersionsAsync is not supported for external versioning");
        return Task.FromResult(new Dictionary<string, List<FileVersion>>());
    }

    public override Task RestoreAsync(string filePath, DateTime versionTime, CancellationToken cancellationToken = default)
    {
        // External versioning does not support restoration
        _logger.LogError("RestoreAsync is not supported for external versioning");
        throw new NotSupportedException("External versioning does not support file restoration");
    }

    public override async Task CleanAsync(CancellationToken cancellationToken = default)
    {
        // External versioning cleanup is handled by the external command
        _logger.LogDebug("External versioning cleanup is handled by the external command");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Expands template variables in the command string
    /// </summary>
    private string ExpandTemplateVariables(string command, string filePath)
    {
        var fullPath = Path.Combine(_folderPath, filePath);
        
        return command
            .Replace("%FOLDER_PATH%", _folderPath)
            .Replace("%FILE_PATH%", fullPath)
            .Replace("%FOLDER_FILESYSTEM%", "basic"); // Default filesystem type
    }

    /// <summary>
    /// Executes the external command asynchronously
    /// </summary>
    private async Task<ExternalCommandResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            // Parse command and arguments
            var (executable, arguments) = ParseCommand(command);
            
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    outputBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for completion with cancellation support
            while (!process.HasExited)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to kill external process");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            var output = outputBuilder.ToString().Trim();
            var error = errorBuilder.ToString().Trim();

            return new ExternalCommandResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = output,
                Error = error
            };
        }
        catch (Exception ex)
        {
            return new ExternalCommandResult
            {
                Success = false,
                ExitCode = -1,
                Output = "",
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Parses a command string into executable and arguments
    /// Handles quoted arguments properly
    /// </summary>
    private (string executable, string arguments) ParseCommand(string command)
    {
        var parts = new List<string>();
        var current = "";
        var inQuotes = false;
        var escapeNext = false;

        foreach (var c in command)
        {
            if (escapeNext)
            {
                current += c;
                escapeNext = false;
                continue;
            }

            if (c == '\\')
            {
                escapeNext = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (c == ' ' && !inQuotes)
            {
                if (!string.IsNullOrEmpty(current))
                {
                    parts.Add(current);
                    current = "";
                }
                continue;
            }

            current += c;
        }

        if (!string.IsNullOrEmpty(current))
        {
            parts.Add(current);
        }

        var executable = parts.FirstOrDefault() ?? "";
        var arguments = string.Join(" ", parts.Skip(1));

        return (executable, arguments);
    }

    /// <summary>
    /// Gets statistics about external versioning (limited info)
    /// </summary>
    public ExternalVersioningStats GetStats()
    {
        return new ExternalVersioningStats
        {
            Command = _command,
            VersionerType = VersionerType
        };
    }
}

/// <summary>
/// Result of executing an external command
/// </summary>
public class ExternalCommandResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Statistics for external versioning (limited)
/// </summary>
public class ExternalVersioningStats
{
    public string Command { get; set; } = string.Empty;
    public string VersionerType { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"external: command='{Command}'";
    }
}