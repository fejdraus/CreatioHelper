using CreatioHelper.Domain.Enums;

namespace CreatioHelper.Application.Interfaces;

public interface IPlatformService
{
    PlatformType GetPlatform();
    bool IsFeatureSupported(string featureName);
    Task<Dictionary<string, object>> GetSystemInfoAsync();
}

public static class FeatureNames
{
    public const string IISManagement = "IISManagement";               // Windows - IIS
    public const string WindowsServiceManagement = "WindowsServiceManagement"; // Windows - Windows Services (Kestrel)
    public const string SystemdManagement = "SystemdManagement";       // Linux - systemd services
    public const string LaunchdManagement = "LaunchdManagement";       // macOS - launchd services  
    public const string FileSync = "FileSync";
    public const string ProcessManagement = "ProcessManagement";
}