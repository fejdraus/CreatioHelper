namespace CreatioHelper.Application.Interfaces;

public class PackageCleanResult
{
    public List<string> InvalidJsonFiles { get; set; } = new();
    public List<string> InvalidOtherJsonFiles { get; set; } = new();
    public bool HasInvalidJson => InvalidJsonFiles.Count > 0;
    public bool HasInvalidOtherJson => InvalidOtherJsonFiles.Count > 0;
    public int OrphanResourcesDeleted { get; set; }
    public int EmptyDirectoriesDeleted { get; set; }
    public int NonMatchingSqlFilesDeleted { get; set; }
    public int FoldersWithoutDescriptorDeleted { get; set; }
    public List<string> CircularDependencies { get; set; } = new();
    public bool HasCircularDependencies => CircularDependencies.Count > 0;
}

public interface IPackageCleaner
{
    PackageCleanResult CleanPackages(string packagesPath);
}