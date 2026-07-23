namespace CreatioHelper.Domain.Entities;

public sealed class ConfigurationBackupPackage
{
    public required string Name { get; init; }
    public long SizeBytes { get; init; }
}

public sealed class ConfigurationBackupRemovalEntry
{
    public required string Name { get; init; }
    public string? UId { get; init; }
    public string? Source { get; init; }
}

public sealed class ConfigurationBackup
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public DateTime? CreatedOn { get; init; }
    public IReadOnlyList<ConfigurationBackupPackage> ChangedPackages { get; init; } = Array.Empty<ConfigurationBackupPackage>();
    public IReadOnlyList<ConfigurationBackupRemovalEntry> PackagesToRemove { get; init; } = Array.Empty<ConfigurationBackupRemovalEntry>();

    public bool IsEmpty => ChangedPackages.Count == 0 && PackagesToRemove.Count == 0;
    public bool CanRestore => Exists && !IsEmpty;
}
