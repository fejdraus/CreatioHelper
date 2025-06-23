using System.Runtime.InteropServices;
using CreatioHelper.Agent.Abstractions;

namespace CreatioHelper.Agent.Services;

public class PlatformService : IPlatformService
{
    private readonly ILogger<PlatformService> _logger;
    private readonly PlatformType _platform;

    public PlatformService(ILogger<PlatformService> logger)
    {
        _logger = logger;
        _platform = DetectPlatform();
        _logger.LogInformation("Detected platform: {Platform}", _platform);
    }

    public PlatformType GetPlatform() => _platform;

    public bool IsFeatureSupported(string featureName)
    {
        return featureName switch
        {
            FeatureNames.IISManagement => _platform == PlatformType.Windows,
            FeatureNames.WindowsServiceManagement => _platform == PlatformType.Windows,
            FeatureNames.SystemdManagement => _platform == PlatformType.Linux,
            FeatureNames.LaunchdManagement => _platform == PlatformType.MacOS,
            FeatureNames.FileSync => true,
            FeatureNames.ProcessManagement => true,
            _ => false
        };
    }

    public async Task<Dictionary<string, object>> GetSystemInfoAsync()
    {
        var info = new Dictionary<string, object>
        {
            ["Platform"] = _platform.ToString(),
            ["OSDescription"] = RuntimeInformation.OSDescription,
            ["Architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["FrameworkDescription"] = RuntimeInformation.FrameworkDescription,
            ["MachineName"] = Environment.MachineName,
            ["UserName"] = Environment.UserName,
            ["ProcessorCount"] = Environment.ProcessorCount,
            ["WorkingSet"] = Environment.WorkingSet,
            ["SupportedFeatures"] = GetSupportedFeatures()
        };

        return await Task.FromResult(info);
    }

    private PlatformType DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return PlatformType.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return PlatformType.Linux;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return PlatformType.MacOS;
    
        return PlatformType.Unknown;
    }

    private List<string> GetSupportedFeatures()
    {
        var features = new List<string> { FeatureNames.FileSync, FeatureNames.ProcessManagement };

        if (IsFeatureSupported(FeatureNames.IISManagement))
            features.Add(FeatureNames.IISManagement);
    
        if (IsFeatureSupported(FeatureNames.WindowsServiceManagement))
            features.Add(FeatureNames.WindowsServiceManagement);
    
        if (IsFeatureSupported(FeatureNames.SystemdManagement))
            features.Add(FeatureNames.SystemdManagement);
    
        if (IsFeatureSupported(FeatureNames.LaunchdManagement))
            features.Add(FeatureNames.LaunchdManagement);

        return features;
    }
}
