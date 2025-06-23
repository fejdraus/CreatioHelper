using CreatioHelper.Agent.Models;

namespace CreatioHelper.Agent.Abstractions;

public interface IWebServerServiceFactory
{
    IWebServerService CreateWebServerService();
    bool IsWebServerSupported();
    Task<string> GetSupportedWebServerTypeAsync();
    List<string> GetAvailableWebServerTypes();
}