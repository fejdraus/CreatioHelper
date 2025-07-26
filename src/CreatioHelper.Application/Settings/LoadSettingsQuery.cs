using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Mediator;

namespace CreatioHelper.Application.Settings;

public record LoadSettingsQuery : IRequest<AppSettings>;
