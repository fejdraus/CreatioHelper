namespace CreatioHelper.Application.Interfaces;

public interface IWorkspacePreparer
{
    Task PrepareAsync(string sitePath, CancellationToken cancellationToken = default);
    void Prepare(string sitePath, out bool quartzIsActiveOriginal);
    void UpdateOutConfig(string configPath, bool quartzIsActive);
    int InstallFromRepository(string sitePath, string packagesPath);
    int RegenerateSchemaSources(string sitePath);
    int RebuildWorkspace(string sitePath);
    int BuildConfiguration(string sitePath, bool force = true);

    int Compile(string sitePath);

    int CompileFast(string sitePath);

    bool SupportsFastCompile(string sitePath);

    int CompileAll(string sitePath);
    int DeletePackages(string sitePath, string packageList);
    int LoadLicResponse(string sitePath, string licFilePath);
    int RestoreConfiguration(string sitePath, string backupPath, bool installPackageData = true, bool ignoreSqlScriptBackwardCompatibilityCheck = false);
    int PrevalidateInstallFromRepository(string sitePath, string packagesPath);
    bool IsFileDesignModeEnabled(string sitePath);
    string GetPkgPath(string sitePath);
    int DownloadPackages(string sitePath, string destinationPath);
    int LoadPackagesToDb(string sitePath);
    int SaveLicenseRequest(string sitePath, string destinationPath, string customerId, string fileName);
}
