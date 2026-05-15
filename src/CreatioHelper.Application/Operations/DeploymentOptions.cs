using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Application.Operations;

public class DeploymentOptions
{
    public string? SitePath { get; set; }
    public Version? SiteVersion { get; set; }
    public bool IsIisMode { get; set; }
    public string? IisSiteName { get; set; }
    public string? IisPoolName { get; set; }
    public string? ServiceName { get; set; }

    public string? PackagesPath { get; set; }
    public string? PackagesToDeleteBefore { get; set; }
    public string? PackagesToDeleteAfter { get; set; }
    public bool PrevalidateBeforeInstall { get; set; }

    public CompileMode Compile { get; set; } = CompileMode.Auto;
    public SyncMode Sync { get; set; } = SyncMode.None;

    public IReadOnlyList<ServerInfo> Servers { get; set; } = Array.Empty<ServerInfo>();
    public bool HasRemoteServers { get; set; }
    public bool SkipRedisClear { get; set; }
    public bool SkipServerRestart { get; set; }

    public ISyncthingMonitorService? SyncthingMonitor { get; set; }
}
