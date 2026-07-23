using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Operations;

public class RestoreConfigurationOptions
{
    public string? SitePath { get; set; }
    public bool IsIisMode { get; set; }
    public string? IisSiteName { get; set; }
    public string? IisPoolName { get; set; }
    public bool IisPoolOnly { get; set; }
    public string? ServiceName { get; set; }

    public bool InstallPackageData { get; set; } = true;
    public bool IgnoreSqlScriptBackwardCompatibilityCheck { get; set; }

    public CompileMode Compile { get; set; } = CompileMode.Incremental;

    public IReadOnlyList<ServerInfo> Servers { get; set; } = Array.Empty<ServerInfo>();
    public bool HasRemoteServers { get; set; }
    public bool SkipRedisClear { get; set; }
}
