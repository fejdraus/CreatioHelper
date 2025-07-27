using System.Diagnostics;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Site;

public class RobocopyFileCopyHelper : IFileCopyHelper
{
    private readonly IOutputWriter _output;
    private readonly IMetricsService _metrics;

    public RobocopyFileCopyHelper(IOutputWriter output, IMetricsService metrics)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public async Task<int> CopyAsync(
        ServerInfo server,
        string sourceDir,
        string destDir,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Robocopy is available only on Windows");

        return await _metrics.MeasureAsync("file_copy_duration", async () =>
        {
            string arguments = $"\"{sourceDir}\" \"{destDir}\" /e /purge /NFL /NDL /NJH /NJS";
            _output.WriteLine($"[INFO][{server.Name?.Value}] Starting copy from {sourceDir} to {destDir}");

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "robocopy",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            var exitCode = process.ExitCode;

            // Robocopy exit codes: 0-7 are success, 8+ are errors
            if (exitCode < 8)
            {
                _output.WriteLine($"[OK][{server.Name?.Value}] Copy completed successfully (exit code: {exitCode})");
                _metrics.IncrementCounter("file_copy_success", new()
                {
                    ["server"] = server.Name?.Value ?? "unknown",
                    ["exit_code"] = exitCode.ToString()
                });
            }
            else
            {
                _output.WriteLine($"[ERROR][{server.Name?.Value}] Copy failed (exit code: {exitCode})");
                _metrics.IncrementCounter("file_copy_error", new()
                {
                    ["server"] = server.Name?.Value ?? "unknown",
                    ["exit_code"] = exitCode.ToString()
                });
            }

            return exitCode;
        }, new()
        {
            ["server"] = server.Name?.Value ?? "unknown",
            ["source"] = Path.GetFileName(sourceDir),
            ["destination"] = Path.GetFileName(destDir)
        });
    }
}
