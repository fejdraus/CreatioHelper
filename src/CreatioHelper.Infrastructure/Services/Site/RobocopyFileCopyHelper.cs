using System.Diagnostics;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Site;

public class RobocopyFileCopyHelper(IOutputWriter output) : IFileCopyHelper
{
    public async Task<int> CopyAsync(
        ServerInfo server,
        string sourceDir,
        string destDir,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Robocopy is available only on Windows");

        string arguments = $"\"{sourceDir}\" \"{destDir}\" /e /purge /NFL /NDL /NJH /NJS";
        output.WriteLine($"[INFO][{server.Name}] Starting copy from {sourceDir} to {destDir}");

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

        try
        {
            process.Start();
            output.WriteLine($"[INFO][{server.Name}] File copying in progress...");

            await process.WaitForExitAsync(cancellationToken);

            int exitCode = process.ExitCode;
            if (exitCode >= 8)
                output.WriteLine($"[ERROR][{server.Name}] Robocopy failed with error code: {exitCode}");
            else if (exitCode > 1)
                output.WriteLine($"[WARN][{server.Name}] Robocopy completed with warning: Code {exitCode}");
            else
                output.WriteLine($"[INFO][{server.Name}] Copying completed from {sourceDir} to {destDir}");

            return exitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                output.WriteLine($"[INFO][{server.Name}] Cancelling Robocopy process...");
                process.Kill();
                output.WriteLine($"[INFO][{server.Name}] Robocopy process terminated.");
            }
            output.WriteLine($"[DEBUG][{server.Name}] Copying cancelled from {sourceDir} to {destDir}");
            return -1;
        }
        catch (Exception ex)
        {
            output.WriteLine($"[ERROR][{server.Name}] Exception during copying: {ex.Message}");
            return -2;
        }
    }
}
