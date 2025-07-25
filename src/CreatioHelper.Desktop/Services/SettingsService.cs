using CreatioHelper.Core;
using CreatioHelper.Core.Services;

namespace CreatioHelper.Services;

public class SettingsService : ISettingsService
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
