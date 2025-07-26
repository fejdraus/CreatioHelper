using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Mediator;

namespace CreatioHelper.Application.Settings;

public class SaveSettingsHandler : IRequestHandler<SaveSettingsCommand, Unit>
{
    private readonly ISettingsService _service;

    public SaveSettingsHandler(ISettingsService service)
    {
        _service = service;
    }

    public Task<Unit> Handle(SaveSettingsCommand request, CancellationToken cancellationToken)
    {
        _service.Save(request.Settings);
        return Task.FromResult(Unit.Value);
    }
}
