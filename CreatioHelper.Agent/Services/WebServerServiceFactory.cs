using CreatioHelper.Core.Abstractions;
using CreatioHelper.Core.Models;
using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Agent.Services.Linux;
using CreatioHelper.Agent.Services.MacOS;

namespace CreatioHelper.Agent.Services;

public class WebServerServiceFactory : IWebServerServiceFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IPlatformService _platformService;
    private readonly IConfigurationService _configurationService;

    public WebServerServiceFactory(IServiceProvider serviceProvider, IPlatformService platformService, 
        IConfigurationService configurationService)
    {
        _serviceProvider = serviceProvider;
        _platformService = platformService;
        _configurationService = configurationService;
    }

    public async Task<IWebServerService> CreateWebServerServiceAsync()
    {
        if (_platformService.IsFeatureSupported(FeatureNames.IISManagement) && OperatingSystem.IsWindows())
        {
            var preferredType = await _configurationService.GetWebServerTypeAsync();

            if (preferredType != null && preferredType.Equals("WindowsService", StringComparison.OrdinalIgnoreCase))
            {
                return _serviceProvider.GetRequiredService<WindowsServiceManager>();
            }

            return _serviceProvider.GetRequiredService<IisManagerService>();
        }
        
        if (_platformService.IsFeatureSupported(FeatureNames.WindowsServiceManagement) && OperatingSystem.IsWindows())
        {
            return _serviceProvider.GetRequiredService<WindowsServiceManager>();
        }
        
        if (_platformService.IsFeatureSupported(FeatureNames.SystemdManagement) && OperatingSystem.IsLinux())
        {
            return _serviceProvider.GetRequiredService<SystemdServiceManager>();
        }
        
        if (_platformService.IsFeatureSupported(FeatureNames.LaunchdManagement) && OperatingSystem.IsMacOS())
        {
            return _serviceProvider.GetRequiredService<LaunchdServiceManager>();
        }
        
        throw new NotSupportedException($"Web server management is not supported on {_platformService.GetPlatform()}");
    }
    
    public async Task<string> GetSupportedWebServerTypeAsync()
    {
        if (_platformService.IsFeatureSupported(FeatureNames.IISManagement) && OperatingSystem.IsWindows())
        {
            var preferredType = await _configurationService.GetWebServerTypeAsync();
            return preferredType != null && preferredType.Equals("WindowsService", StringComparison.OrdinalIgnoreCase) ? "WindowsService/Kestrel" : "IIS";
        }
        
        if (_platformService.IsFeatureSupported(FeatureNames.SystemdManagement) && OperatingSystem.IsLinux())
            return "Systemd/Kestrel";
        
        if (_platformService.IsFeatureSupported(FeatureNames.LaunchdManagement) && OperatingSystem.IsMacOS())
            return "Launchd/Kestrel";
        
        return "None";
    }

    public bool IsWebServerSupported()
    {
        return (_platformService.IsFeatureSupported(FeatureNames.IISManagement) && OperatingSystem.IsWindows()) ||
               (_platformService.IsFeatureSupported(FeatureNames.WindowsServiceManagement) && OperatingSystem.IsWindows()) ||
               (_platformService.IsFeatureSupported(FeatureNames.SystemdManagement) && OperatingSystem.IsLinux()) ||
               (_platformService.IsFeatureSupported(FeatureNames.LaunchdManagement) && OperatingSystem.IsMacOS());
    }

    public List<string> GetAvailableWebServerTypes()
    {
        var types = new List<string>();
        
        if (_platformService.IsFeatureSupported(FeatureNames.IISManagement) && OperatingSystem.IsWindows())
        {
            types.Add("IIS");
            types.Add("WindowsService/Kestrel");
        }
        
        if (_platformService.IsFeatureSupported(FeatureNames.SystemdManagement) && OperatingSystem.IsLinux())
            types.Add("Systemd/Kestrel");
        
        if (_platformService.IsFeatureSupported(FeatureNames.LaunchdManagement) && OperatingSystem.IsMacOS())
            types.Add("Launchd/Kestrel");
        
        return types;
    }
}
