using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services.MacOS;

public class MacOsSiteSynchronizer : SshSiteSynchronizerBase
{
    public MacOsSiteSynchronizer(IOutputWriter output, IFileCopyHelper fileCopyHelper)
        : base(output, fileCopyHelper)
    {
    }

    protected override string BuildStopCommand(string serviceName) =>
        $"sudo launchctl stop {serviceName} 2>/dev/null || sudo systemctl stop {serviceName}";

    protected override string BuildStartCommand(string serviceName) =>
        $"sudo launchctl start {serviceName} 2>/dev/null || sudo systemctl start {serviceName}";
}
