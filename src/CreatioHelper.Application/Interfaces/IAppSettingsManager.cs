namespace CreatioHelper.Application.Interfaces;

using Domain.Entities;

public interface IAppSettingsManager
{
    AppSettings Load();
    void Save(AppSettings settings);
}
