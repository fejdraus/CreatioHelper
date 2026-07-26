using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Linux;

public class LinuxSiteSynchronizer : SshSiteSynchronizerBase
{
    public LinuxSiteSynchronizer(IOutputWriter output, IFileCopyHelper fileCopyHelper)
        : base(output, fileCopyHelper)
    {
    }

    protected override string BuildStopCommand(string serviceName) =>
        $"sudo systemctl stop {serviceName}";

    protected override string BuildStartCommand(string serviceName) =>
        $"sudo systemctl start {serviceName}";
}
