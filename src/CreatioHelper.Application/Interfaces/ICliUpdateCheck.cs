using CreatioHelper.Domain.Enums;

namespace CreatioHelper.Application.Interfaces;

public interface ICliUpdateCheck
{
    string CurrentVersion { get; }

    Task<string?> GetNewerVersionAsync(UpdateChannel channel, CancellationToken cancellationToken = default);
}
