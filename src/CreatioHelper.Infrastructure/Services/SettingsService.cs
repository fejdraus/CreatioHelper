using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services;

public class SettingsService : ISettingsService
{
    private readonly IAppSettingsManager _manager;

    public SettingsService(IAppSettingsManager manager)
    {
        _manager = manager;
    }

    public AppSettings Load() => _manager.Load();

    public void Save(AppSettings settings) => _manager.Save(settings);
}
