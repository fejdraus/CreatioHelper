using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.UnitTests.Operations.Fakes;

internal sealed class RecordingWorkspacePreparer : IWorkspacePreparer
{
    public bool QuartzReturn { get; set; } = true;
    public Exception? PrepareException { get; set; }

    public string? LastPrepareSitePath { get; private set; }
    public int PrepareCount { get; private set; }
    public List<(string Path, bool Quartz)> UpdateOutConfigInvocations { get; } = new();

    public Task PrepareAsync(string sitePath, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void Prepare(string sitePath, out bool quartzIsActiveOriginal)
    {
        PrepareCount++;
        LastPrepareSitePath = sitePath;
        quartzIsActiveOriginal = QuartzReturn;
        if (PrepareException != null)
        {
            throw PrepareException;
        }
    }

    public void UpdateOutConfig(string configPath, bool quartzIsActive)
        => UpdateOutConfigInvocations.Add((configPath, quartzIsActive));

    public int InstallFromRepository(string sitePath, string packagesPath) => 0;
    public int RegenerateSchemaSources(string sitePath) => 0;
    public int RebuildWorkspace(string sitePath) => 0;
    public int BuildConfiguration(string sitePath, bool force = true) => 0;
    public int Compile(string sitePath) => 0;
    public int CompileFast(string sitePath) => 0;
    public bool SupportsFastCompile(string sitePath) => false;
    public int CompileAll(string sitePath) => 0;
    public int DeletePackages(string sitePath, string packageList) => 0;
    public int LoadLicResponse(string sitePath, string licFilePath) => 0;
    public int RestoreConfiguration(string sitePath, string backupPath, bool installPackageData = true, bool ignoreSqlScriptBackwardCompatibilityCheck = false) => 0;
    public int PrevalidateInstallFromRepository(string sitePath, string packagesPath) => 0;
    public bool IsFileDesignModeEnabled(string sitePath) => false;
    public string GetPkgPath(string sitePath) => string.Empty;
    public int DownloadPackages(string sitePath, string destinationPath) => 0;
    public int LoadPackagesToDb(string sitePath) => 0;
    public int SaveLicenseRequest(string sitePath, string destinationPath, string customerId, string fileName) => 0;
}
