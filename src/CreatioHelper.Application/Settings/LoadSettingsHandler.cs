using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Mediator;

namespace CreatioHelper.Application.Settings;

public class LoadSettingsHandler : IRequestHandler<LoadSettingsQuery, AppSettings>
{
    private readonly ISettingsService _service;

    public LoadSettingsHandler(ISettingsService service)
    {
        _service = service;
    }

    public Task<AppSettings> Handle(LoadSettingsQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_service.Load());
    }
}
