using CreatioHelper.Application.Services.Updates;

namespace CreatioHelper.Application.Interfaces;

public interface IUpdateService
{
    UpdateState State { get; }

    string CurrentVersion { get; }

    string? LastSeenVersion { get; }

    event EventHandler<UpdateState>? StateChanged;

    void Start();

    Task CheckNowAsync(bool explicitly = true, CancellationToken cancellationToken = default);

    Task DownloadAndInstallAsync(CancellationToken cancellationToken = default);

    void QuitAndApply();

    void SkipCurrentAvailable();
}
