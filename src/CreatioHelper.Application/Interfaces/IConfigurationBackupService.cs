using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface IConfigurationBackupService
{
    string GetBackupPath(string sitePath);

    ConfigurationBackup Read(string sitePath);

    bool IsRestoreSupported(string sitePath);
}
