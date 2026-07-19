using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface IWebServerServiceFactory
{
    Task<IWebServerService> CreateWebServerServiceAsync();
    Task<IWebServerService> CreateWebServerServiceForSiteAsync(WebSiteInfo site);
    bool IsWebServerSupported();
    Task<string> GetSupportedWebServerTypeAsync();
    List<string> GetAvailableWebServerTypes();
}