namespace CreatioHelper.Application.Interfaces;

using CreatioHelper.Domain.Entities;

public interface IAppSettingsManager
{
    AppSettings Load();
    void Save(AppSettings settings);
}
