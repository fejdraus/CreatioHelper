using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Infrastructure.Services.Workspace;

public class ConfigurationBackupService : IConfigurationBackupService
{
    private const string DeleteListFileName = "DeleteList.txt";

    public string GetBackupPath(string sitePath)
    {
        if (string.IsNullOrWhiteSpace(sitePath))
        {
            throw new ArgumentNullException(nameof(sitePath));
        }

        return CreatioSiteLayout.GetConfigurationBackupPath(sitePath);
    }

    public bool IsRestoreSupported(string sitePath)
    {
        var version = CreatioSiteLayout.GetSiteVersion(sitePath);
        return version != null && version >= Constants.MinimumVersionForRestoreConfiguration;
    }

    public ConfigurationBackup Read(string sitePath)
    {
        string backupPath = GetBackupPath(sitePath);

        if (!Directory.Exists(backupPath))
        {
            return new ConfigurationBackup { Path = backupPath, Exists = false };
        }

        var packages = new List<ConfigurationBackupPackage>();
        foreach (var file in Directory.EnumerateFiles(backupPath, "*.gz"))
        {
            var info = new FileInfo(file);
            packages.Add(new ConfigurationBackupPackage
            {
                Name = Path.GetFileNameWithoutExtension(file),
                SizeBytes = info.Length
            });
        }

        packages.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        return new ConfigurationBackup
        {
            Path = backupPath,
            Exists = true,
            CreatedOn = GetCreatedOn(backupPath),
            ChangedPackages = packages,
            PackagesToRemove = ReadRemovalEntries(Path.Combine(backupPath, DeleteListFileName))
        };
    }

    private static DateTime? GetCreatedOn(string backupPath)
    {
        DateTime? latest = null;
        foreach (var file in Directory.EnumerateFiles(backupPath))
        {
            var written = File.GetLastWriteTime(file);
            if (latest == null || written > latest)
            {
                latest = written;
            }
        }

        return latest ?? Directory.GetLastWriteTime(backupPath);
    }

    private static IReadOnlyList<ConfigurationBackupRemovalEntry> ReadRemovalEntries(string deleteListPath)
    {
        if (!File.Exists(deleteListPath))
        {
            return Array.Empty<ConfigurationBackupRemovalEntry>();
        }

        var entries = new List<ConfigurationBackupRemovalEntry>();
        foreach (var line in File.ReadAllLines(deleteListPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',');
            entries.Add(new ConfigurationBackupRemovalEntry
            {
                UId = parts.Length > 0 ? parts[0].Trim() : null,
                Name = parts.Length > 1 ? parts[1].Trim() : line.Trim(),
                Source = parts.Length > 2 ? parts[2].Trim() : null
            });
        }

        return entries;
    }
}
