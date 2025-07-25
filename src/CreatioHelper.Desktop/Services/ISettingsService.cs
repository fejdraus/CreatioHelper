using CreatioHelper.Core;

namespace CreatioHelper.Services;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
