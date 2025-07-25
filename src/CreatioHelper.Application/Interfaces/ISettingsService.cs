using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
