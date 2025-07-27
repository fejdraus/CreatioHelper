namespace CreatioHelper.Application.Interfaces;

public interface IWebServerServiceFactory
{
    Task<IWebServerService> CreateWebServerServiceAsync();
    bool IsWebServerSupported();
    Task<string> GetSupportedWebServerTypeAsync();
    List<string> GetAvailableWebServerTypes();
}