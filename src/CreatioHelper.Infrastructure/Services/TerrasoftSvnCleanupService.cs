using System;
using System.IO;
using System.Threading.Tasks;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services;

public class TerrasoftSvnCleanupService : ITerrasoftSvnCleanupService
{
    private readonly IOutputWriter _output;

    public TerrasoftSvnCleanupService(IOutputWriter output)
    {
        _output = output;
    }

    public Task<bool> CleanupAsync(string sitePath)
    {
        if (string.IsNullOrWhiteSpace(sitePath) || !Directory.Exists(sitePath))
        {
            _output.WriteLine("[ERROR] SVN cleanup: site path is missing or does not exist.");
            return Task.FromResult(false);
        }

        var pkgPath = ResolvePkgPath(sitePath);
        if (pkgPath is null)
        {
            _output.WriteLine("[ERROR] SVN cleanup: 'Pkg' folder not found.");
            return Task.FromResult(false);
        }

        _output.WriteLine($"[INFO] Scanning: {pkgPath}");

        int deleted = 0;
        int failed = 0;

        foreach (var crtDir in Directory.EnumerateDirectories(pkgPath, "Crt*", SearchOption.TopDirectoryOnly))
        {
            foreach (var svnDir in Directory.EnumerateDirectories(crtDir, ".svn", SearchOption.AllDirectories))
            {
                try
                {
                    Directory.Delete(svnDir, recursive: true);
                    _output.WriteLine($"  Deleted: {svnDir}");
                    deleted++;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"  [ERROR] Failed to delete '{svnDir}': {ex.Message}");
                    failed++;
                }
            }
        }

        if (deleted == 0 && failed == 0)
        {
            _output.WriteLine("[OK] No .svn folders found in Crt* packages.");
        }
        else
        {
            _output.WriteLine($"[OK] SVN cleanup completed: {deleted} deleted, {failed} failed.");
        }

        return Task.FromResult(failed == 0);
    }

    private static string? ResolvePkgPath(string sitePath)
    {
        if (Path.GetFileName(sitePath).Equals("Pkg", StringComparison.OrdinalIgnoreCase))
        {
            return sitePath;
        }

        foreach (var dir in Directory.EnumerateDirectories(sitePath, "Pkg", SearchOption.AllDirectories))
        {
            return dir;
        }

        return null;
    }
}
