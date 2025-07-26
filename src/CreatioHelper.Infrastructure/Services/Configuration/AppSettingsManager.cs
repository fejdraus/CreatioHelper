using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Configuration;

public class AppSettingsManager : IAppSettingsManager
{
    public AppSettings Load()
    {
        return AppSettingsService.SettingsFileExists()
            ? AppSettingsService.Load()
            : new AppSettings { IsIisMode = true };
    }

    public void Save(AppSettings settings)
    {
        AppSettingsService.Save(settings);
    }
}
