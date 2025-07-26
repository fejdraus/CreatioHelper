namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Предоставляет методы для подготовки и сборки рабочего пространства приложения.
/// </summary>
public interface IWorkspacePreparer
{
    void Prepare(string sitePath, out bool quartzIsActiveOriginal);
    void UpdateOutConfig(string configPath, bool quartzIsActive);
    int InstallFromRepository(string sitePath, string packagesPath);
    int RegenerateSchemaSources(string sitePath);
    int RebuildWorkspace(string sitePath);
    int BuildConfiguration(string sitePath);
    int DeletePackages(string sitePath, string packageList);
}
