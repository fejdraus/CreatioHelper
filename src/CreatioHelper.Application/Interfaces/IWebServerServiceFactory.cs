using System.Collections.Generic;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

public interface IWebServerServiceFactory
{
    Task<IWebServerService> CreateWebServerServiceAsync();
    bool IsWebServerSupported();
    Task<string> GetSupportedWebServerTypeAsync();
    List<string> GetAvailableWebServerTypes();
}